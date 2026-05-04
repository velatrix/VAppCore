using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VAppCore;

public class VAppCoreConcurrencyOptions
{
    /// <summary>
    /// When true, registers <see cref="LoggingConcurrencyConflictObserver"/> so every concurrency
    /// conflict is logged at Warning level via ILogger. Default false.
    /// </summary>
    public bool LogConflicts { get; set; } = false;
}

public static class ConcurrencyServiceCollectionExtensions
{
    /// <summary>
    /// Registers VAppCore's concurrency support: <see cref="ConcurrencyConflictInterceptor"/> and
    /// either the no-op or logging observer based on <see cref="VAppCoreConcurrencyOptions"/>.
    /// Plug your own observers via <c>services.AddSingleton&lt;IConcurrencyConflictObserver, MyObserver&gt;()</c>
    /// for OpenTelemetry, Prometheus, custom metrics, etc.
    /// </summary>
    /// <remarks>
    /// You also need to wire <see cref="ConcurrencyConflictInterceptor"/> into your DbContext options:
    /// <code>
    /// services.AddDbContext&lt;TDb&gt;((sp, options) =&gt;
    /// {
    ///     options.UseNpgsql(connStr);
    ///     options.UseVAppCore&lt;TDb, Guid, Guid&gt;(sp);
    ///     options.AddInterceptors(sp.GetRequiredService&lt;ConcurrencyConflictInterceptor&gt;());
    /// });
    /// </code>
    /// </remarks>
    public static IServiceCollection AddVAppCoreConcurrency(
        this IServiceCollection services,
        Action<VAppCoreConcurrencyOptions>? configure = null)
    {
        var opts = new VAppCoreConcurrencyOptions();
        configure?.Invoke(opts);

        services.TryAddScoped<ConcurrencyConflictInterceptor>();

        if (opts.LogConflicts)
            services.AddSingleton<IConcurrencyConflictObserver, LoggingConcurrencyConflictObserver>();

        // Always register the NoOp as an extra observer so DI's IEnumerable<IConcurrencyConflictObserver>
        // is non-empty even if no other observer is configured. Multiple observers run sequentially.
        // (Skipped if any observer is already registered? — let DI return all registered ones; NoOp
        // running is harmless.)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConcurrencyConflictObserver, NoOpConcurrencyConflictObserver>());

        return services;
    }
}
