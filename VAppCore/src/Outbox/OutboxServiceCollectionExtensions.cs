using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace VAppCore;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers VAppCore's transactional outbox: <see cref="OutboxInterceptor"/> (writes events as outbox rows
    /// in the same SaveChanges as the entity changes) and <see cref="OutboxProcessor"/> (background hosted service
    /// that polls/dispatches/retries/dead-letters/prunes).
    /// Pair with <see cref="AddDomainEventHandlers"/> to register your handler classes from your project's assembly.
    /// </summary>
    /// <remarks>
    /// You also need to wire <see cref="OutboxInterceptor"/> into your DbContext's options. The simplest way:
    /// <code>
    /// services.AddDbContext&lt;TDb&gt;((sp, options) =&gt;
    /// {
    ///     options.UseNpgsql(connStr);
    ///     options.UseVAppCore&lt;TDb, Guid, Guid&gt;(sp);
    ///     options.AddInterceptors(sp.GetRequiredService&lt;OutboxInterceptor&gt;());
    /// });
    /// </code>
    /// And add a DbSet&lt;OutboxMessage&gt; to your DbContext (plus an EF migration).
    /// </remarks>
    public static IServiceCollection AddVAppCoreOutbox<TDbContext>(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
        where TDbContext : DbContext
    {
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<OutboxOptions>(_ => { });

        services.TryAddSingleton<OutboxInterceptor>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }

    /// <summary>
    /// Scans the assembly for all classes implementing <see cref="IDomainEventHandler{TEvent}"/>
    /// and registers each as a Scoped service for its closed-generic interface. The outbox processor
    /// resolves them via <c>IServiceProvider.GetServices</c> on each dispatch.
    /// </summary>
    public static IServiceCollection AddDomainEventHandlers(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>))
                {
                    services.AddScoped(iface, type);
                }
            }
        }
        return services;
    }
}
