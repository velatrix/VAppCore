using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>One controller action (per HTTP method) or hub method, and what it declares.</summary>
/// <param name="Key"><c>"GET /api/orders/{id}"</c> — route constraints dropped, literal segments lower-cased (routing
/// ignores their case) — or <c>"OrdersHub.Join"</c>.</param>
/// <param name="DisplayName"><c>"Orders.Get"</c> (controller and action) or the hub method's key.</param>
/// <param name="Parameters">The route's parameters, or the hub method's parameters.</param>
/// <param name="Problems">Why <see cref="VAuthorizationEndpointRouteBuilderExtensions.VerifyVAuthorization"/> would
/// refuse this endpoint; empty when it is fully declared.</param>
public sealed record VEndpointDescription(
    string Key,
    string DisplayName,
    bool IsHubMethod,
    bool AllowsAnonymous,
    IReadOnlyList<VAuthorizeAttribute> Declarations,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> Problems)
{
    public override string ToString() => Key;
}

/// <summary>
/// Every controller action and hub method an app serves, with its <see cref="VAuthorizeAttribute"/> declarations —
/// for a test that holds them against the app's own requirement table, and for
/// <see cref="VAuthorizationEndpointRouteBuilderExtensions.VerifyVAuthorization"/>. Minimal-API endpoints are not
/// listed: the declarations are MVC and hub filters, which do not run for them.
/// </summary>
public static partial class VAuthorizationCatalog
{
    /// <summary>What the running app serves (its <see cref="EndpointDataSource"/>).</summary>
    public static IReadOnlyList<VEndpointDescription> Describe(IServiceProvider services) =>
        Describe(services.GetRequiredService<EndpointDataSource>().Endpoints, services);

    /// <summary>The actions and hub methods among <paramref name="endpoints"/>.</summary>
    public static IReadOnlyList<VEndpointDescription> Describe(IEnumerable<Endpoint> endpoints, IServiceProvider services)
    {
        var options = services.GetService<IOptions<VAuthorizationOptions>>()?.Value ?? new VAuthorizationOptions();
        var described = new List<VEndpointDescription>();
        var hubs = new HashSet<Type>();

        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<HubMetadata>() is { } hub)
            {
                hubs.Add(hub.HubType);
                continue;
            }

            if (endpoint is not RouteEndpoint route || endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action)
                continue;

            var declarations = endpoint.Metadata.OfType<VAuthorizeAttribute>().ToList();
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var parameters = route.RoutePattern.Parameters
                .Select(p => p.Name)
                .Where(name => !route.RoutePattern.RequiredValues.ContainsKey(name))
                .ToList();
            var problems = ProblemsOf(declarations, anonymous, parameters, options, isHubMethod: false);
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];

            foreach (var method in methods)
                described.Add(new VEndpointDescription(
                    $"{method} {Normalize(route.RoutePattern.RawText ?? "")}",
                    $"{action.ControllerName}.{action.ActionName}",
                    IsHubMethod: false, anonymous, declarations, parameters, problems));
        }

        foreach (var hub in hubs.OrderBy(h => h.FullName, StringComparer.Ordinal))
        {
            foreach (var method in HubMethods(hub))
            {
                var declarations = VAuthorizeHubFilter.DeclarationsOf(hub, method);
                var anonymous = VAuthorizeHubFilter.AllowsAnonymous(hub, method);
                var parameters = method.GetParameters().Select(p => p.Name!).ToList();
                var name = method.GetCustomAttribute<HubMethodNameAttribute>()?.Name ?? method.Name;
                described.Add(new VEndpointDescription(
                    $"{hub.Name}.{name}", $"{hub.Name}.{name}",
                    IsHubMethod: true, anonymous, declarations, parameters,
                    ProblemsOf(declarations, anonymous, parameters, options, isHubMethod: true)));
            }
        }

        return described.OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every problem <see cref="VAuthorizationEndpointRouteBuilderExtensions.VerifyVAuthorization"/> refuses, one line
    /// each: the endpoints' own, and a scoped declaration with no <see cref="IScopeAccessResolver"/> to answer it.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<VEndpointDescription> described, IServiceProvider services)
    {
        var problems = described.SelectMany(d => d.Problems.Select(p => $"{d.Key} [{d.DisplayName}]: {p}")).ToList();

        var scoped = described.Any(d => d.Declarations.Any(a => a.Scope is not null));
        var resolver = services.GetService<IServiceProviderIsService>()?.IsService(typeof(IScopeAccessResolver)) ?? true;
        if (scoped && !resolver)
            problems.Add($"Declarations name scopes, but no {nameof(IScopeAccessResolver)} is registered.");

        return problems;
    }

    /// <summary>
    /// <c>"api/Orders/{id:guid}"</c> → <c>"/api/orders/{id}"</c>: constraints, defaults, catch-all and optional markers
    /// dropped; literal segments lower-cased.
    /// </summary>
    public static string Normalize(string rawTemplate) =>
        "/" + string.Join('/', Parameter().Replace(rawTemplate.TrimStart('~').TrimStart('/'), "{$1}")
            .Split('/')
            .Select(segment => segment.StartsWith('{') ? segment : segment.ToLowerInvariant()));

    private static List<string> ProblemsOf(
        IReadOnlyList<VAuthorizeAttribute> declarations,
        bool anonymous,
        IReadOnlyList<string> parameters,
        VAuthorizationOptions options,
        bool isHubMethod)
    {
        var problems = new List<string>();
        if (declarations.Count == 0)
        {
            if (!anonymous)
                problems.Add("declares no [VAuthorize] and does not allow anonymous access");
            return problems;
        }
        if (anonymous)
            problems.Add("declares [VAuthorize] but also allows anonymous access, which wins");

        var has = new HashSet<string>(parameters, StringComparer.OrdinalIgnoreCase);
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unbound = new HashSet<string>(declarations.SelectMany(d => d.Unbound ?? []), StringComparer.OrdinalIgnoreCase);

        foreach (var name in unbound.Where(u => !has.Contains(u)))
            problems.Add($"Unbound names '{name}', which is not a {(isHubMethod ? "parameter" : "route value")} of this endpoint");

        var scoped = false;
        foreach (var declaration in declarations.Where(d => d.Scope is not null))
        {
            var registration = options.FindScope(declaration.Scope!);
            if (registration is null)
            {
                problems.Add($"names scope '{declaration.Scope}', which is not registered");
                continue;
            }
            scoped = true;

            if (registration.RouteValue is null)
            {
                if (declaration.ScopeFrom is not null)
                    problems.Add($"reads scope '{registration.Type}' from '{declaration.ScopeFrom}', but that scope has no id");
                continue;
            }

            var from = declaration.ScopeFrom ?? registration.RouteValue;
            if (!has.Contains(from))
                problems.Add($"reads scope '{registration.Type}' from '{from}', which is not a {(isHubMethod ? "parameter" : "route value")} of this endpoint");

            accounted.Add(from);
            accounted.Add(registration.RouteValue);
            accounted.UnionWith(registration.Entities.Keys);
        }

        // A route value of a scoped endpoint is an id the caller chose: it must name the scope, belong to it (a
        // registered entity), or be declared Unbound. Hub parameters are mostly data, and are not held to this.
        if (scoped && !isHubMethod)
            foreach (var parameter in parameters.Where(p => !accounted.Contains(p) && !unbound.Contains(p)))
                problems.Add($"route value '{parameter}' is not bound to the declared scope (register it as an entity of the scope, or declare it Unbound)");

        return problems;
    }

    /// <summary>The methods SignalR lets a client invoke: public instance methods not declared by <see cref="Hub"/>.</summary>
    private static IEnumerable<MethodInfo> HubMethods(Type hub) =>
        hub.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition)
            .Where(m => m.GetBaseDefinition().DeclaringType is { } declaring
                && declaring != typeof(object)
                && !(declaring.IsGenericType ? declaring.GetGenericTypeDefinition() == typeof(Hub<>) : declaring == typeof(Hub)))
            .Where(m => m.Name != nameof(IDisposable.Dispose))
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    [GeneratedRegex(@"\{\*{0,2}(\w+)(?:[:=][^}]*)?\??\}")]
    private static partial Regex Parameter();
}
