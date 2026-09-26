using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// What <see cref="VAuthorizeFilter"/> and <see cref="VAuthorizeHubFilter"/> both enforce, in this order:
/// <list type="number">
///   <item>not signed in → 401;</item>
///   <item>per declaration: its scope — from a route value or hub argument, directly or through an entity (an entity
///     that does not exist → the entity's 404); a scope the caller may not see → the scope's 404, or the entity's when
///     the scope was reached through one, so a foreign id and a never-issued one answer alike; a missing permission →
///     403;</item>
///   <item>then every other value registered as an entity of a declared scope must belong to it → else the entity's
///     404. Binding comes after the permission check, so a caller refused for a permission learns nothing about
///     ids.</item>
/// </list>
/// </summary>
internal static class VAuthorizationEnforcer
{
    public static async Task EnforceAsync(
        IServiceProvider services,
        ICurrentUser currentUser,
        ClaimsPrincipal caller,
        IReadOnlyList<VAuthorizeAttribute> declarations,
        Func<string, string?> valueOf,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedError(new ErrorObject
            {
                Message = "Authentication required",
                MessageKey = "server.errors.unauthenticated"
            });

        var options = services.GetService<IOptions<VAuthorizationOptions>>()?.Value ?? new VAuthorizationOptions();
        VAccess? access = null;
        List<Located>? located = null;
        HashSet<string>? unbound = null;

        foreach (var declaration in declarations)
        {
            if (declaration.Unbound is { Length: > 0 })
                (unbound ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(declaration.Unbound);

            if (declaration.Scope is null)
            {
                CheckFlat(declaration, currentUser, options);
                continue;
            }

            var registration = options.Scope(declaration.Scope);
            var here = await LocateAsync(registration, declaration, valueOf, services, cancellationToken);

            access ??= services.GetRequiredService<VAccess>().For(caller);
            var granted = await access.GetAsync(here.Scope, cancellationToken);
            if (!granted.IsVisible)
                throw NotFound(here.Through?.NotFound ?? registration.NotFound);

            CheckScoped(declaration, here.Scope, granted, currentUser, caller, options);

            located ??= [];
            var first = located.FirstOrDefault(l => l.Registration == registration);
            if (first is null)
                located.Add(here);
            else if (first.Scope != here.Scope)
                // Two declarations of one scope type must name the same scope: a run of another project under this
                // project's route is not in this project, whatever the caller may do there.
                throw NotFound(here.Through?.NotFound ?? registration.NotFound);
        }

        if (located is null)
            return;

        foreach (var here in located)
        {
            foreach (var entity in here.Registration.Entities.Values)
            {
                if (string.Equals(entity.Key, here.Through?.Key, StringComparison.OrdinalIgnoreCase)
                    || unbound?.Contains(entity.Key) == true)
                    continue;

                var id = valueOf(entity.Key);
                if (id is null)
                    continue;

                var owner = await entity.LocateScopeAsync(services, id, cancellationToken);
                if (owner is null || owner != here.Scope.Id)
                    throw NotFound(entity.NotFound);
            }
        }
    }

    public static NotFoundError NotFound(ErrorObject error) =>
        new(new ErrorObject { Message = error.Message, MessageKey = error.MessageKey, Metadata = error.Metadata });

    private sealed record Located(VScopeRegistration Registration, VScope Scope, VEntityRegistration? Through);

    private static async Task<Located> LocateAsync(
        VScopeRegistration registration,
        VAuthorizeAttribute declaration,
        Func<string, string?> valueOf,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (registration.RouteValue is null)
        {
            if (declaration.ScopeFrom is not null)
                throw new InvalidOperationException(
                    $"[VAuthorize] reads scope '{registration.Type}' from '{declaration.ScopeFrom}', but that scope has no id.");
            return new Located(registration, new VScope(registration.Type, null), null);
        }

        var from = declaration.ScopeFrom ?? registration.RouteValue;
        var raw = valueOf(from);

        if (registration.Entities.TryGetValue(from, out var entity))
        {
            var owner = raw is null ? null : await entity.LocateScopeAsync(services, raw, cancellationToken);
            if (owner is null)
                throw NotFound(entity.NotFound);

            // Reached through an entity on a route that also names the scope: both must name the same scope.
            if (valueOf(registration.RouteValue) is { } named && registration.Canonicalize(named) != owner)
                throw NotFound(entity.NotFound);

            return new Located(registration, new VScope(registration.Type, owner), entity);
        }

        var id = raw is null ? null : registration.Canonicalize(raw);
        if (id is null)
            throw NotFound(registration.NotFound);
        return new Located(registration, new VScope(registration.Type, id), null);
    }

    private static void CheckScoped(
        VAuthorizeAttribute declaration,
        VScope scope,
        ScopeAccess granted,
        ICurrentUser currentUser,
        ClaimsPrincipal caller,
        VAuthorizationOptions options)
    {
        if (declaration.ApiKey is not null)
        {
            if (caller.Identity?.AuthenticationType != options.ApiKeyAuthenticationType)
                throw ApiKeyRequired(declaration.ApiKey);
            if (!granted.Has(declaration.ApiKey))
                throw new ForbiddenError(options.ForbiddenError(new VForbidden(scope, [declaration.ApiKey], AnyOf: false)));
        }

        if (declaration.Permission is not null && !granted.Has(declaration.Permission))
            throw new ForbiddenError(options.ForbiddenError(new VForbidden(scope, [declaration.Permission], AnyOf: false)));

        if (declaration.AnyOf is { Length: > 0 } anyOf && !anyOf.Any(granted.Has))
            throw new ForbiddenError(options.ForbiddenError(new VForbidden(scope, anyOf, AnyOf: true)));

        if (declaration.Role is not null && !currentUser.IsInRole(declaration.Role))
            throw RoleRequired(declaration.Role);
    }

    /// <summary>The flat permission list — VAppCore's behaviour before scopes, unchanged.</summary>
    private static void CheckFlat(VAuthorizeAttribute declaration, ICurrentUser currentUser, VAuthorizationOptions options)
    {
        if (declaration.ApiKey is not null)
        {
            if (currentUser.AuthenticationType != options.ApiKeyAuthenticationType)
                throw ApiKeyRequired(declaration.ApiKey);
            if (!currentUser.HasPermission(declaration.ApiKey))
                throw new ForbiddenError(new ErrorObject
                {
                    Message = $"Required permission: {declaration.ApiKey}",
                    MessageKey = "permission.required",
                    Metadata = new { permission = declaration.ApiKey }
                });
            return;
        }

        if (declaration.Role is not null && !currentUser.IsInRole(declaration.Role))
            throw RoleRequired(declaration.Role);

        if (declaration.Permission is not null && !currentUser.HasPermission(declaration.Permission))
            throw new ForbiddenError(new ErrorObject
            {
                Message = $"Required permission: {declaration.Permission}",
                MessageKey = "server.errors.forbidden"
            });

        if (declaration.AnyOf is { Length: > 0 } anyOf && !anyOf.Any(currentUser.HasPermission))
            throw new ForbiddenError(new ErrorObject
            {
                Message = $"Required permission: one of {string.Join(", ", anyOf)}",
                MessageKey = "server.errors.forbidden"
            });
    }

    private static ForbiddenError ApiKeyRequired(string permission) => new(new ErrorObject
    {
        Message = "API key authentication required",
        MessageKey = "api_key.required",
        Metadata = new { permission }
    });

    private static ForbiddenError RoleRequired(string role) => new(new ErrorObject
    {
        Message = $"Required role: {role}",
        MessageKey = "server.errors.forbidden"
    });
}

/// <summary>
/// <see cref="ICurrentUser"/> read straight from a principal's claims, under <see cref="VAppCoreOptions"/>' claim
/// names — for hub invocations, and for apps that register <see cref="VAuthorizationServiceCollectionExtensions.AddVAuthorization"/>
/// without an <see cref="ICurrentUser"/> of their own.
/// </summary>
internal sealed class PrincipalCurrentUser(ClaimsPrincipal principal, VAppCoreOptions options) : ICurrentUser
{
    public bool IsAuthenticated => principal.Identity?.IsAuthenticated ?? false;
    public string? AuthenticationType => principal.Identity?.AuthenticationType;

    public bool IsInRole(string role) =>
        principal.FindAll(options.RoleClaim).Any(c => string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    public bool HasPermission(string permission) =>
        principal.FindAll(options.PermissionClaim).Any(c => string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase));

    public static ICurrentUser For(IServiceProvider services, ClaimsPrincipal principal) =>
        new PrincipalCurrentUser(principal, services.GetService<IOptions<VAppCoreOptions>>()?.Value ?? new VAppCoreOptions());
}
