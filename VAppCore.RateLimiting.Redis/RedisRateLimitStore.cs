using StackExchange.Redis;

namespace VAppCore;

/// <summary>
/// Redis-backed token-bucket rate-limit store. Implements the same <see cref="IRateLimitStore"/>
/// interface as the in-memory default, but state lives in Redis so all app instances enforce
/// limits against the same counter.
/// Atomicity is guaranteed via a Lua script (single round-trip, no lock).
/// </summary>
public sealed class RedisRateLimitStore : IRateLimitStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _keyPrefix;

    /// <summary>
    /// Lua atomically: refill bucket based on elapsed time, decrement if cost ≤ available,
    /// return [permitted (1/0), remaining tokens, retry-after seconds (0 if permitted)].
    /// KEYS[1] = bucket key
    /// ARGV[1] = capacity, ARGV[2] = refill_per_sec, ARGV[3] = cost, ARGV[4] = now (unix seconds, float), ARGV[5] = peek_only (0/1)
    /// </summary>
    private static readonly string ConsumeScript = @"
local key = KEYS[1]
local capacity = tonumber(ARGV[1])
local refill = tonumber(ARGV[2])
local cost = tonumber(ARGV[3])
local now = tonumber(ARGV[4])
local peek_only = tonumber(ARGV[5])

local data = redis.call('HMGET', key, 'tokens', 'last_refill')
local tokens = tonumber(data[1])
local last_refill = tonumber(data[2])

if tokens == nil then
  tokens = capacity
  last_refill = now
end

local elapsed = now - last_refill
if elapsed > 0 then
  tokens = math.min(capacity, tokens + elapsed * refill)
  last_refill = now
end

if tokens >= cost then
  if peek_only == 0 then
    tokens = tokens - cost
    redis.call('HMSET', key, 'tokens', tokens, 'last_refill', last_refill)
    -- Set TTL: enough time for the bucket to fully refill (so untouched buckets expire)
    if refill > 0 then
      local ttl_seconds = math.ceil(capacity / refill) + 60
      redis.call('EXPIRE', key, ttl_seconds)
    end
  end
  return {1, tokens, 0}
else
  local deficit = cost - tokens
  local retry = refill > 0 and (deficit / refill) or 99999
  -- Update last_refill so deferred reads see correct state
  if peek_only == 0 then
    redis.call('HMSET', key, 'tokens', tokens, 'last_refill', last_refill)
    if refill > 0 then
      local ttl_seconds = math.ceil(capacity / refill) + 60
      redis.call('EXPIRE', key, ttl_seconds)
    end
  end
  return {0, tokens, retry}
end
";

    public RedisRateLimitStore(IConnectionMultiplexer redis, string keyPrefix = "vappcore:rl:")
    {
        _redis = redis;
        _keyPrefix = keyPrefix;
    }

    public Task<RateLimitResult> TryConsumeAsync(string partitionKey, RateLimitPolicy policy, int cost = 1)
        => RunScriptAsync(partitionKey, policy, cost, peekOnly: false);

    public Task<RateLimitResult> PeekAsync(string partitionKey, RateLimitPolicy policy, int cost = 1)
        => RunScriptAsync(partitionKey, policy, cost, peekOnly: true);

    private async Task<RateLimitResult> RunScriptAsync(string partitionKey, RateLimitPolicy policy, int cost, bool peekOnly)
    {
        var db = _redis.GetDatabase();
        var key = $"{_keyPrefix}{policy.Name}:{partitionKey}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        var result = (RedisResult[])(await db.ScriptEvaluateAsync(ConsumeScript, [key], [
            policy.Capacity,
            policy.RefillTokensPerSecond,
            cost,
            now,
            peekOnly ? 1 : 0
        ]))!;

        var permitted = (long)result[0] == 1;
        if (permitted) return new RateLimitResult(true, null);

        var retrySeconds = (double)result[2];
        return new RateLimitResult(false, TimeSpan.FromSeconds(retrySeconds));
    }
}
