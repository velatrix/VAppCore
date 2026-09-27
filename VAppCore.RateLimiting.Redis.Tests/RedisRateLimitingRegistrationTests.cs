using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VAppCore.RateLimiting.Redis.Tests;

public class RedisRateLimitingRegistrationTests
{
    [Fact]
    public void AddVAppCoreRateLimitingRedis_ReplacesInMemoryStore_InServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVAppCoreRateLimiting();

        // Before Redis registration: in-memory store is registered (by a factory, since 3.1.0 — it takes the
        // options' sweep interval and a registered TimeProvider)
        var before = services.Where(s => s.ServiceType == typeof(IRateLimitStore)).ToList();
        Assert.Single(before);
        using (var provider = services.BuildServiceProvider())
            Assert.IsType<MemoryRateLimitStore>(provider.GetRequiredService<IRateLimitStore>());

        // Pretend to register Redis (we use a connection string that's never actually connected
        // because we won't build the provider — just verify the descriptor swap)
        services.AddVAppCoreRateLimitingRedis("localhost:6379");

        var after = services.Where(s => s.ServiceType == typeof(IRateLimitStore)).ToList();
        Assert.Single(after);
        // Memory store is gone, replaced with a factory-registered RedisRateLimitStore
        Assert.DoesNotContain(before[0], after);
        Assert.NotNull(after[0].ImplementationFactory);
    }
}

// NOTE on test coverage: the actual RedisRateLimitStore behavior (Lua-script atomic decrement,
// retry-after computation, key expiry) requires a live Redis instance to test meaningfully.
// We don't run those here — Hub's CI exercises them once the consumer wires it up.
// This file only verifies the DI swap is correct so consumers can trust that calling
// AddVAppCoreRateLimitingRedis(...) actually replaces the in-memory store.
