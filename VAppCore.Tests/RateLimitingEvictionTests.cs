using Microsoft.Extensions.DependencyInjection;

namespace VAppCore.Tests;

/// <summary>
/// 3.1.0: <see cref="MemoryRateLimitStore"/> evicts the buckets that have refilled to capacity — no different from new
/// ones — so a stream of new partition keys no longer grows memory without bound; a bucket with a deficit stays, and an
/// eviction never grants a token twice. Refill follows the monotonic clock.
/// </summary>
public class RateLimitingEvictionTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task FullBuckets_AreEvicted_AtTheNextSweep_AndBehaveAsNew()
    {
        var time = new ManualTime();
        var store = new MemoryRateLimitStore(time, Minute);
        var policy = new RateLimitPolicy("test", Capacity: 2, RefillTokensPerSecond: 1);

        await store.TryConsumeAsync("drained", policy);
        await store.TryConsumeAsync("drained", policy);
        await store.PeekAsync("untouched", policy);
        Assert.Equal(2, store.Count);

        // Within the interval nothing is swept, full or not.
        time.Advance(TimeSpan.FromSeconds(30));
        await store.TryConsumeAsync("recent", policy);
        Assert.Equal(3, store.Count);

        // The first request after the interval sweeps: the refilled and the untouched go; the one it just drew stays.
        time.Advance(TimeSpan.FromSeconds(31));
        await store.TryConsumeAsync("recent", policy);
        Assert.Equal(1, store.Count);

        // An evicted partition starts over as a new bucket would: full.
        Assert.True((await store.TryConsumeAsync("drained", policy)).Permitted);
        Assert.True((await store.TryConsumeAsync("drained", policy)).Permitted);
        Assert.False((await store.TryConsumeAsync("drained", policy)).Permitted);
    }

    [Fact]
    public async Task ABucketWithADeficit_IsKept()
    {
        var time = new ManualTime();
        var store = new MemoryRateLimitStore(time, Minute);
        var noRefill = new RateLimitPolicy("test", Capacity: 1, RefillTokensPerSecond: 0);
        var slow = new RateLimitPolicy("slow", Capacity: 3, RefillTokensPerSecond: 1.0 / 600);

        await store.TryConsumeAsync("a", noRefill);
        await store.TryConsumeAsync("a", slow);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(0, store.EvictFullBuckets());
        Assert.False((await store.TryConsumeAsync("a", noRefill)).Permitted);

        // Refilled after all (a token per ten minutes), the slow one goes; the one without refill never does.
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, store.EvictFullBuckets());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Sweeps_AtMostOncePerInterval()
    {
        var time = new ManualTime();
        var store = new MemoryRateLimitStore(time, Minute);
        var policy = new RateLimitPolicy("test", Capacity: 5, RefillTokensPerSecond: 1);

        await store.PeekAsync("a", policy);
        time.Advance(Minute);
        await store.PeekAsync("b", policy);   // sweeps: a and b are full
        Assert.Equal(0, store.Count);

        await store.PeekAsync("c", policy);
        time.Advance(TimeSpan.FromSeconds(59));
        await store.PeekAsync("d", policy);   // not a minute since the last sweep
        Assert.Equal(2, store.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        await store.PeekAsync("e", policy);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task AnEvictionNeverGrantsATokenTwice()
    {
        // Without refill a bucket is full only until it is first drawn from, so every eviction races the first draw:
        // a caller that took the bucket before the sweep must not draw from the evicted one and then again from its
        // successor.
        var store = new MemoryRateLimitStore(TimeProvider.System, Minute);
        var policy = new RateLimitPolicy("test", Capacity: 3, RefillTokensPerSecond: 0);
        using var stop = new CancellationTokenSource();
        var sweeper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                store.EvictFullBuckets();
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 300; i++)
        {
            var key = $"k{i}";
            var draws = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => store.TryConsumeAsync(key, policy))));
            Assert.Equal(3, draws.Count(d => d.Permitted));
        }

        await stop.CancelAsync();
        await sweeper;
    }

    [Fact]
    public async Task Refill_FollowsTheMonotonicClock_NotTheWallClock()
    {
        var time = new ManualTime();
        var store = new MemoryRateLimitStore(time, Minute);
        var policy = new RateLimitPolicy("test", Capacity: 2, RefillTokensPerSecond: 1);

        await store.TryConsumeAsync("a", policy);
        await store.TryConsumeAsync("a", policy);

        // The wall clock steps back an hour and forward a day; a second passes.
        time.SetWall(time.GetUtcNow().AddHours(-1));
        time.SetWall(time.GetUtcNow().AddDays(1));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True((await store.TryConsumeAsync("a", policy)).Permitted);
        Assert.False((await store.TryConsumeAsync("a", policy)).Permitted);
    }

    [Fact]
    public async Task Registration_UsesTheRegisteredClock_AndTheSweepInterval()
    {
        var time = new ManualTime();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddVAppCoreRateLimiting(o => o.MemoryStoreSweepInterval = TimeSpan.FromSeconds(10));
        using var provider = services.BuildServiceProvider();
        var store = Assert.IsType<MemoryRateLimitStore>(provider.GetRequiredService<IRateLimitStore>());
        var policy = new RateLimitPolicy("test", Capacity: 1, RefillTokensPerSecond: 1);

        await store.PeekAsync("a", policy);
        time.Advance(TimeSpan.FromSeconds(10));
        await store.PeekAsync("b", policy);
        Assert.Equal(0, store.Count);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddVAppCoreRateLimiting(o => o.MemoryStoreSweepInterval = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryRateLimitStore(time, TimeSpan.Zero));
    }

    /// <summary>A clock the test moves by hand; its wall time can also be set on its own.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _wall = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => _wall;

        public void Advance(TimeSpan by)
        {
            Interlocked.Add(ref _timestamp, by.Ticks);
            _wall += by;
        }

        public void SetWall(DateTimeOffset wall) => _wall = wall;
    }
}
