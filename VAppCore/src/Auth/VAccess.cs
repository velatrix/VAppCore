using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// <see cref="IVAccess"/> for one request or hub invocation: asks the <see cref="IScopeAccessResolver"/> at most once
/// per scope and keeps the answer until the scope ends. Calls are serialized, so a resolver that reads a DbContext is
/// never entered twice at once.
/// </summary>
internal sealed class VAccess : IVAccess, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IHttpContextAccessor? _http;
    private readonly VAuthorizationOptions _options;
    private readonly Dictionary<VScope, ScopeAccess> _known = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaimsPrincipal? _caller;

    public VAccess(IServiceProvider services, IOptions<VAuthorizationOptions> options)
    {
        _services = services;
        _http = services.GetService<IHttpContextAccessor>();
        _options = options.Value;
    }

    /// <summary>
    /// The caller the filters authorized — set by them, since a hub invocation has no ambient HTTP context. Outside a
    /// filter the request's user is used.
    /// </summary>
    internal VAccess For(ClaimsPrincipal caller)
    {
        if (!ReferenceEquals(_caller, caller))
        {
            _caller = caller;
            _known.Clear();
        }
        return this;
    }

    private ClaimsPrincipal Caller =>
        _caller ?? _http?.HttpContext?.User
        ?? throw new InvalidOperationException("IVAccess has no caller: it is used outside a request or hub invocation.");

    public async ValueTask<ScopeAccess> GetAsync(VScope scope, CancellationToken cancellationToken = default)
    {
        scope = Canonical(scope);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_known.TryGetValue(scope, out var known))
                return known;

            var resolver = _services.GetService<IScopeAccessResolver>()
                ?? throw new InvalidOperationException(
                    $"Scope '{scope.Type}' needs an {nameof(IScopeAccessResolver)} registered in DI.");
            // A resolver that answers nothing has not granted anything.
            var access = await resolver.ResolveAsync(Caller, scope, cancellationToken) ?? ScopeAccess.Hidden;
            _known[scope] = access;
            return access;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ScopeAccess> RequireAsync(VScope scope, string? permission = null, CancellationToken cancellationToken = default)
    {
        var registration = _options.Scope(scope.Type);
        var access = await GetAsync(scope, cancellationToken);
        if (!access.IsVisible)
            throw VAuthorizationEnforcer.NotFound(registration.NotFound);
        if (permission is not null && !access.Has(permission))
            throw new ForbiddenError(_options.ForbiddenError(new VForbidden(Canonical(scope), [permission], AnyOf: false)));
        return access;
    }

    public async ValueTask<ScopeAccess> RequireAnyAsync(VScope scope, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var registration = _options.Scope(scope.Type);
        var access = await GetAsync(scope, cancellationToken);
        if (!access.IsVisible)
            throw VAuthorizationEnforcer.NotFound(registration.NotFound);
        if (permissions.Count > 0 && !permissions.Any(access.Has))
            throw new ForbiddenError(_options.ForbiddenError(new VForbidden(Canonical(scope), permissions, AnyOf: true)));
        return access;
    }

    /// <summary>Ids of a registered scope in their canonical form, so one scope is resolved once however it was written.</summary>
    private VScope Canonical(VScope scope) =>
        scope.Id is not null && _options.FindScope(scope.Type) is { RouteValue: not null } registration
            && registration.Canonicalize(scope.Id) is { } canonical
            ? scope with { Id = canonical }
            : scope;

    public void Dispose() => _gate.Dispose();
}
