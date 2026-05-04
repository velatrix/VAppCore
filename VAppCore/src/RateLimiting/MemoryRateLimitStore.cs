using System.Collections.Concurrent;

namespace VAppCore;

/// <summary>
/// In-process rate-limit store. Per-bucket state lives in a thread-safe dictionary keyed
/// by (policyName, partitionKey). Per-process only — multi-instance deploys need the
/// Redis-backed store from the <c>VAppCore.RateLimiting.Redis</c> sub-package.
/// </summary>
public sealed class MemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();

    public Task<RateLimitResult> TryConsumeAsync(string partitionKey, RateLimitPolicy policy, int cost = 1)
    {
        var key = $"{policy.Name}:{partitionKey}";
        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(policy));
        return Task.FromResult(bucket.TryConsume(cost));
    }

    public Task<RateLimitResult> PeekAsync(string partitionKey, RateLimitPolicy policy, int cost = 1)
    {
        var key = $"{policy.Name}:{partitionKey}";
        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(policy));
        return Task.FromResult(bucket.Peek(cost));
    }

    private sealed class TokenBucket
    {
        private readonly RateLimitPolicy _policy;
        private readonly object _lock = new();
        private double _tokens;
        private DateTimeOffset _lastRefill;

        public TokenBucket(RateLimitPolicy policy)
        {
            _policy = policy;
            _tokens = policy.Capacity;
            _lastRefill = DateTimeOffset.UtcNow;
        }

        public RateLimitResult TryConsume(int cost)
        {
            lock (_lock)
            {
                Refill();
                if (_tokens >= cost)
                {
                    _tokens -= cost;
                    return new RateLimitResult(true, null);
                }
                return BuildRejection(cost);
            }
        }

        public RateLimitResult Peek(int cost)
        {
            lock (_lock)
            {
                Refill();
                return _tokens >= cost
                    ? new RateLimitResult(true, null)
                    : BuildRejection(cost);
            }
        }

        private void Refill()
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = (now - _lastRefill).TotalSeconds;
            _tokens = Math.Min(_policy.Capacity, _tokens + elapsed * _policy.RefillTokensPerSecond);
            _lastRefill = now;
        }

        private RateLimitResult BuildRejection(int cost)
        {
            var deficit = cost - _tokens;
            var retryAfter = _policy.RefillTokensPerSecond > 0
                ? TimeSpan.FromSeconds(deficit / _policy.RefillTokensPerSecond)
                : TimeSpan.MaxValue;
            return new RateLimitResult(false, retryAfter);
        }
    }
}
