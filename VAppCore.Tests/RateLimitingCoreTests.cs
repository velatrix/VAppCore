namespace VAppCore.Tests;

public class RateLimitingCoreTests
{
    [Fact]
    public async Task FirstRequestUpToCapacity_AllPermitted()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 5, RefillTokensPerSecond: 0); // no refill

        for (int i = 0; i < 5; i++)
        {
            var res = await store.TryConsumeAsync("user-1", policy);
            Assert.True(res.Permitted, $"request {i + 1} should be permitted");
        }
    }

    [Fact]
    public async Task RequestPastCapacity_Rejected_WithRetryAfter()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 3, RefillTokensPerSecond: 1.0); // 1 token/sec refill

        // Drain
        for (int i = 0; i < 3; i++) await store.TryConsumeAsync("user-1", policy);

        // 4th request — rejected
        var res = await store.TryConsumeAsync("user-1", policy);
        Assert.False(res.Permitted);
        Assert.NotNull(res.RetryAfter);
        Assert.True(res.RetryAfter.Value.TotalSeconds is > 0 and <= 2);
    }

    [Fact]
    public async Task DifferentPartitions_Independent()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 1, RefillTokensPerSecond: 0);

        var a1 = await store.TryConsumeAsync("user-A", policy);
        var b1 = await store.TryConsumeAsync("user-B", policy);
        var a2 = await store.TryConsumeAsync("user-A", policy);

        Assert.True(a1.Permitted);
        Assert.True(b1.Permitted);
        Assert.False(a2.Permitted);  // user-A drained, user-B unaffected
    }

    [Fact]
    public async Task DifferentPolicies_SamePartition_Independent()
    {
        var store = new MemoryRateLimitStore();
        var policyA = new RateLimitPolicy("a", Capacity: 1, RefillTokensPerSecond: 0);
        var policyB = new RateLimitPolicy("b", Capacity: 1, RefillTokensPerSecond: 0);

        var aRes = await store.TryConsumeAsync("user-1", policyA);
        var bRes = await store.TryConsumeAsync("user-1", policyB);

        Assert.True(aRes.Permitted);
        Assert.True(bRes.Permitted);
    }

    [Fact]
    public async Task CostHigherThanOne_DecrementsMultiple()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 10, RefillTokensPerSecond: 0);

        var first = await store.TryConsumeAsync("user-1", policy, cost: 7);
        Assert.True(first.Permitted);

        // 3 tokens left — cost 5 should reject
        var second = await store.TryConsumeAsync("user-1", policy, cost: 5);
        Assert.False(second.Permitted);

        // cost 3 should pass
        var third = await store.TryConsumeAsync("user-1", policy, cost: 3);
        Assert.True(third.Permitted);
    }

    [Fact]
    public async Task PeekAsync_DoesNotConsume()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 2, RefillTokensPerSecond: 0);

        var peek1 = await store.PeekAsync("user-1", policy);
        var peek2 = await store.PeekAsync("user-1", policy);
        var peek3 = await store.PeekAsync("user-1", policy);

        // All peeks should report Permitted (no decrement)
        Assert.True(peek1.Permitted);
        Assert.True(peek2.Permitted);
        Assert.True(peek3.Permitted);

        // Real consumes should still have full bucket available
        Assert.True((await store.TryConsumeAsync("user-1", policy)).Permitted);
        Assert.True((await store.TryConsumeAsync("user-1", policy)).Permitted);
        Assert.False((await store.TryConsumeAsync("user-1", policy)).Permitted);
    }

    [Fact]
    public async Task PeekAsync_ReportsRejection_WhenBucketEmpty()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 1, RefillTokensPerSecond: 1);

        await store.TryConsumeAsync("user-1", policy);
        var peek = await store.PeekAsync("user-1", policy);

        Assert.False(peek.Permitted);
        Assert.NotNull(peek.RetryAfter);
    }

    [Fact]
    public async Task TokensRefillOverTime()
    {
        var store = new MemoryRateLimitStore();
        var policy = new RateLimitPolicy("test", Capacity: 2, RefillTokensPerSecond: 10); // very fast refill

        // Drain
        await store.TryConsumeAsync("user-1", policy);
        await store.TryConsumeAsync("user-1", policy);

        // Wait for refill
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken); // 3 tokens worth at 10/sec

        var afterWait = await store.TryConsumeAsync("user-1", policy);
        Assert.True(afterWait.Permitted);
    }
}
