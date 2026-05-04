using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace VAppCore;

public static class RedisRateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the in-memory <see cref="IRateLimitStore"/> with a Redis-backed implementation.
    /// Call AFTER <see cref="RateLimitingServiceCollectionExtensions.AddVAppCoreRateLimiting"/>.
    /// Connection multiplexer is registered as a singleton from the supplied connection string.
    /// </summary>
    public static IServiceCollection AddVAppCoreRateLimitingRedis(
        this IServiceCollection services,
        string connectionString,
        string keyPrefix = "vappcore:rl:")
    {
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        services.RemoveAll<IRateLimitStore>();
        services.AddSingleton<IRateLimitStore>(sp => new RedisRateLimitStore(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            keyPrefix));
        return services;
    }

    /// <summary>
    /// Variant that takes a pre-built <see cref="IConnectionMultiplexer"/> — useful when Redis
    /// is shared with other components (caching, pub/sub, etc.) so you don't open a second connection.
    /// </summary>
    public static IServiceCollection AddVAppCoreRateLimitingRedis(
        this IServiceCollection services,
        IConnectionMultiplexer redis,
        string keyPrefix = "vappcore:rl:")
    {
        services.RemoveAll<IRateLimitStore>();
        services.AddSingleton<IRateLimitStore>(_ => new RedisRateLimitStore(redis, keyPrefix));
        return services;
    }
}
