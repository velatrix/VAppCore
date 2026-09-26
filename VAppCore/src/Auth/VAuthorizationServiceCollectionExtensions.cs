using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VAppCore;

public static class VAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Enforces <see cref="VAuthorizeAttribute"/> on every controller action (<see cref="VAuthorizeFilter"/>) and hub
    /// method (<see cref="VAuthorizeHubFilter"/>), and registers <see cref="IVAccess"/>. Standalone — it does not need
    /// <c>AddVAppCore</c>, which calls it. Safe to call more than once: each <paramref name="configure"/> adds to the
    /// same options. Declarations that name a scope also need an <see cref="IScopeAccessResolver"/> in DI.
    /// </summary>
    public static IServiceCollection AddVAuthorization(
        this IServiceCollection services,
        Action<VAuthorizationOptions>? configure = null)
    {
        services.AddOptions<VAuthorizationOptions>();
        if (configure is not null)
            services.Configure(configure);

        if (services.Any(d => d.ServiceType == typeof(Registered)))
            return services;
        services.AddSingleton<Registered>();

        services.AddHttpContextAccessor();
        services.TryAddScoped<VAccess>();
        services.TryAddScoped<IVAccess>(sp => sp.GetRequiredService<VAccess>());
        services.Configure<MvcOptions>(o => o.Filters.Add<VAuthorizeFilter>());
        services.Configure<HubOptions>(o => o.AddFilter<VAuthorizeHubFilter>());
        return services;
    }

    /// <summary>Marks the one-time registrations as done.</summary>
    private sealed class Registered;
}

public static class VAuthorizationEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Refuses to start the app — throws, listing every problem — unless every controller action and hub method mapped
    /// so far declares its requirement (<see cref="VAuthorizeAttribute"/>, or anonymous access), every declared scope is
    /// registered and read from a value the endpoint has, every route value of a scoped endpoint is bound to its scope
    /// or declared <see cref="VAuthorizeAttribute.Unbound"/>, and an <see cref="IScopeAccessResolver"/> is registered when
    /// any declaration names a scope. Call it after mapping, before <c>Run</c>.
    /// </summary>
    public static IEndpointRouteBuilder VerifyVAuthorization(this IEndpointRouteBuilder endpoints)
    {
        var described = VAuthorizationCatalog.Describe(
            endpoints.DataSources.SelectMany(source => source.Endpoints), endpoints.ServiceProvider);
        var problems = VAuthorizationCatalog.Problems(described, endpoints.ServiceProvider);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"VAuthorize declarations are incomplete ({problems.Count}):\n" + string.Join("\n", problems));
        return endpoints;
    }
}
