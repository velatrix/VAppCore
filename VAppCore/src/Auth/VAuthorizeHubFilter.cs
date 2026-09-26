using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace VAppCore;

/// <summary>
/// Global SignalR hub filter that enforces <see cref="VAuthorizeAttribute"/> declarations on hub methods (and on the
/// hub class, for all its methods) with the same rules as <see cref="VAuthorizeFilter"/>; a declaration's
/// <see cref="VAuthorizeAttribute.ScopeFrom"/> names a parameter of the method. Registered by
/// <see cref="VAuthorizationServiceCollectionExtensions.AddVAuthorization"/>.
/// </summary>
/// <remarks>
/// A refusal reaches the client as a <see cref="HubException"/> whose message is the refusal's message key
/// (<c>permission.required</c>, or the scope's or entity's not-found key) — the key a client can branch on, and no more
/// than the equivalent HTTP answer says. Flat (unscoped) checks read the connection's claims.
/// </remarks>
public sealed class VAuthorizeHubFilter : IHubFilter
{
    private static readonly ConcurrentDictionary<(Type Hub, MethodInfo Method), Declared> Methods = new();

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var declared = Methods.GetOrAdd((invocationContext.Hub.GetType(), invocationContext.HubMethod), Describe);
        if (declared.Declarations.Count > 0 && !declared.AllowsAnonymous)
        {
            var services = invocationContext.ServiceProvider;
            var caller = invocationContext.Context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
            var arguments = invocationContext.HubMethodArguments;
            try
            {
                await VAuthorizationEnforcer.EnforceAsync(
                    services,
                    PrincipalCurrentUser.For(services, caller),
                    caller,
                    declared.Declarations,
                    name => declared.Parameters.TryGetValue(name, out var index) && index < arguments.Count && arguments[index] is { } value
                        ? Convert.ToString(value, CultureInfo.InvariantCulture)
                        : null,
                    invocationContext.Context.ConnectionAborted);
            }
            catch (BaseError refused)
            {
                throw new HubException(refused.Context.Error.MessageKey);
            }
        }

        return await next(invocationContext);
    }

    /// <summary>The declarations on a hub method, the hub's own first, as the catalog reads them.</summary>
    internal static IReadOnlyList<VAuthorizeAttribute> DeclarationsOf(Type hub, MethodInfo method) =>
        [.. hub.GetCustomAttributes<VAuthorizeAttribute>(inherit: true), .. method.GetCustomAttributes<VAuthorizeAttribute>(inherit: true)];

    internal static bool AllowsAnonymous(Type hub, MethodInfo method) =>
        hub.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any()
        || method.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

    private static Declared Describe((Type Hub, MethodInfo Method) key) => new(
        DeclarationsOf(key.Hub, key.Method),
        AllowsAnonymous(key.Hub, key.Method),
        key.Method.GetParameters()
            .Where(p => p.Name is not null)
            .ToDictionary(p => p.Name!, p => p.Position, StringComparer.OrdinalIgnoreCase));

    private sealed record Declared(
        IReadOnlyList<VAuthorizeAttribute> Declarations,
        bool AllowsAnonymous,
        IReadOnlyDictionary<string, int> Parameters);
}
