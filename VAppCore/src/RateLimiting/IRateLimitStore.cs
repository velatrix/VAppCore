namespace VAppCore;

/// <summary>
/// Backing store for rate-limit token buckets. Default implementation
/// <see cref="MemoryRateLimitStore"/> uses an in-process ConcurrentDictionary; for
/// horizontally-scaled deploys, register a Redis-backed implementation from the
/// <c>VAppCore.RateLimiting.Redis</c> sub-package instead.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Attempts to consume <paramref name="cost"/> tokens from the bucket identified by
    /// (policy.Name, partitionKey). Returns Permitted=true and decrements on success;
    /// Permitted=false with RetryAfter populated on failure.
    /// </summary>
    Task<RateLimitResult> TryConsumeAsync(string partitionKey, RateLimitPolicy policy, int cost = 1);

    /// <summary>
    /// Inspects whether <paramref name="cost"/> tokens COULD be consumed right now,
    /// without actually decrementing. Use for "should I let the user start this expensive
    /// operation" UX flows. Beware: result is advisory — by the time you act on it,
    /// another request may have consumed tokens.
    /// </summary>
    Task<RateLimitResult> PeekAsync(string partitionKey, RateLimitPolicy policy, int cost = 1);
}
