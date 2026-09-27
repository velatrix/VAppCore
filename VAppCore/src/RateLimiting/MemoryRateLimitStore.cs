using System.Collections.Concurrent;

namespace VAppCore;

/// <summary>
/// In-process rate-limit store. Per-bucket state lives in a thread-safe dictionary keyed
/// by (policyName, partitionKey). Per-process only — multi-instance deploys need the
/// Redis-backed store from the <c>VAppCore.RateLimiting.Redis</c> sub-package.
/// <para>
/// A bucket that has refilled to capacity is exactly what a new bucket would be, so it is dropped: at most once per
/// <see cref="VAppCoreRateLimitingOptions.MemoryStoreSweepInterval"/>, the request that finds the interval passed
/// evicts every full bucket. Memory follows the recent request rate — a stream of new partition keys (addresses, emails)
/// no longer grows it without bound — and a bucket that still holds a deficit is never forgotten. A caller holding a
/// bucket the sweep has just evicted takes a fresh one, so an eviction never grants a token twice.
/// </para>
/// <para>
/// Refill is measured on <see cref="TimeProvider.GetTimestamp"/>, a monotonic clock: a wall-clock step back takes no
/// tokens away, and a step forward grants none.
/// </para>
/// </summary>
public sealed class MemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly TimeProvider _time;
    private readonly long _sweepEvery;
    private long _nextSweep;
    private int _sweeping;

    public MemoryRateLimitStore()
        : this(TimeProvider.System, VAppCoreRateLimitingOptions.DefaultMemoryStoreSweepInterval)
    {
    }

    /// <param name="time">The clock refill and sweeps are measured on (its monotonic timestamp).</param>
    /// <param name="sweepInterval">How often, at most, full buckets are evicted.</param>
    public MemoryRateLimitStore(TimeProvider time, TimeSpan sweepInterval)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (sweepInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sweepInterval), sweepInterval, "The sweep interval must be positive.");

        _time = time;
        _sweepEvery = (long)(sweepInterval.TotalSeconds * time.TimestampFrequency);
        _nextSweep = time.GetTimestamp() + _sweepEvery;
    }

    /// <summary>The buckets held now — full ones among them until the next sweep evicts them.</summary>
    public int Count => _buckets.Count;

    public Task<RateLimitResult> TryConsumeAsync(string partitionKey, RateLimitPolicy policy, int cost = 1) =>
        Task.FromResult(Use(partitionKey, policy, cost, consume: true));

    public Task<RateLimitResult> PeekAsync(string partitionKey, RateLimitPolicy policy, int cost = 1) =>
        Task.FromResult(Use(partitionKey, policy, cost, consume: false));

    /// <summary>
    /// Evicts every bucket that has refilled to capacity — what the periodic sweep does — and answers how many went. A
    /// bucket with a deficit stays.
    /// </summary>
    public int EvictFullBuckets()
    {
        var now = _time.GetTimestamp();
        var evicted = 0;
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.TryEvictIfFull(now) && _buckets.TryRemove(new KeyValuePair<string, TokenBucket>(key, bucket)))
                evicted++;
        }
        return evicted;
    }

    private RateLimitResult Use(string partitionKey, RateLimitPolicy policy, int cost, bool consume)
    {
        var key = $"{policy.Name}:{partitionKey}";
        RateLimitResult? result;
        while (true)
        {
            var now = _time.GetTimestamp();
            var bucket = _buckets.GetOrAdd(key, static (_, state) => new TokenBucket(state.Policy, state.Time, state.Now),
                (Policy: policy, Time: _time, Now: now));
            result = consume ? bucket.TryConsume(cost, now) : bucket.Peek(cost, now);
            if (result is not null)
                break;

            // The sweep evicted this bucket after it was taken: drop it if it is still listed, and take a fresh one.
            _buckets.TryRemove(new KeyValuePair<string, TokenBucket>(key, bucket));
        }

        SweepIfDue();
        return result;
    }

    /// <summary>The first request after the interval passes sweeps; any other request at that moment does not wait.</summary>
    private void SweepIfDue()
    {
        var now = _time.GetTimestamp();
        if (now < Volatile.Read(ref _nextSweep) || Interlocked.CompareExchange(ref _sweeping, 1, 0) != 0)
            return;
        try
        {
            Volatile.Write(ref _nextSweep, now + _sweepEvery);
            EvictFullBuckets();
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private sealed class TokenBucket(RateLimitPolicy policy, TimeProvider time, long created)
    {
        private readonly object _lock = new();
        private double _tokens = policy.Capacity;
        private long _lastRefill = created;
        private bool _evicted;

        /// <summary>Null once the sweep has evicted the bucket: the caller takes a fresh one.</summary>
        public RateLimitResult? TryConsume(int cost, long now)
        {
            lock (_lock)
            {
                if (_evicted)
                    return null;
                Refill(now);
                if (_tokens >= cost)
                {
                    _tokens -= cost;
                    return new RateLimitResult(true, null);
                }
                return BuildRejection(cost);
            }
        }

        /// <summary>Null once the sweep has evicted the bucket: the caller takes a fresh one.</summary>
        public RateLimitResult? Peek(int cost, long now)
        {
            lock (_lock)
            {
                if (_evicted)
                    return null;
                Refill(now);
                return _tokens >= cost
                    ? new RateLimitResult(true, null)
                    : BuildRejection(cost);
            }
        }

        /// <summary>Marks the bucket evicted when it has refilled to capacity — no different from a new one.</summary>
        public bool TryEvictIfFull(long now)
        {
            lock (_lock)
            {
                if (_evicted)
                    return true;
                Refill(now);
                _evicted = _tokens >= policy.Capacity;
                return _evicted;
            }
        }

        private void Refill(long now)
        {
            if (now <= _lastRefill)
                return;
            var elapsed = time.GetElapsedTime(_lastRefill, now).TotalSeconds;
            _tokens = Math.Min(policy.Capacity, _tokens + elapsed * policy.RefillTokensPerSecond);
            _lastRefill = now;
        }

        private RateLimitResult BuildRejection(int cost)
        {
            var deficit = cost - _tokens;
            var retryAfter = policy.RefillTokensPerSecond > 0
                ? TimeSpan.FromSeconds(deficit / policy.RefillTokensPerSecond)
                : TimeSpan.MaxValue;
            return new RateLimitResult(false, retryAfter);
        }
    }
}
