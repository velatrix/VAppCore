using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VAppCore;

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Registers VAppCore's rate-limiting services: in-memory store, default partitioner,
    /// the three default policies (vauth/vmutation/vread), and observers.
    /// Replace the store via <c>services.AddSingleton&lt;IRateLimitStore, RedisRateLimitStore&gt;()</c>
    /// (from the <c>VAppCore.RateLimiting.Redis</c> sub-package) for distributed deployments.
    /// </summary>
    public static IServiceCollection AddVAppCoreRateLimiting(
        this IServiceCollection services,
        Action<VAppCoreRateLimitingOptions>? configure = null)
    {
        var opts = new VAppCoreRateLimitingOptions();
        configure?.Invoke(opts);

        services.Configure<VAppCoreRateLimitingOptions>(o =>
        {
            o.Policies.Clear();
            foreach (var (k, v) in opts.Policies) o.Policies[k] = v;
            o.TierMultipliers.Clear();
            foreach (var (k, v) in opts.TierMultipliers) o.TierMultipliers[k] = v;
            o.LogRejections = opts.LogRejections;
        });

        services.TryAddSingleton<IRateLimitStore, MemoryRateLimitStore>();
        services.TryAddSingleton<IRateLimitPartitioner, DefaultRateLimitPartitioner>();
        services.TryAddScoped<RateLimitChecker>();

        if (opts.LogRejections)
            services.AddSingleton<IRateLimitObserver, LoggingRateLimitObserver>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRateLimitObserver, NoOpRateLimitObserver>());

        return services;
    }

    /// <summary>
    /// Adds the <see cref="VRateLimitMiddleware"/> to the pipeline. Must be called AFTER
    /// <c>UseRouting()</c> and BEFORE the endpoints, so the middleware can read endpoint metadata.
    /// </summary>
    public static IApplicationBuilder UseVRateLimiting(this IApplicationBuilder app)
        => app.UseMiddleware<VRateLimitMiddleware>();
}
