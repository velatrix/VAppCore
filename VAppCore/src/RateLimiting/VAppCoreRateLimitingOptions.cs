namespace VAppCore;

public class VAppCoreRateLimitingOptions
{
    /// <summary>
    /// Named policies to enforce. Pre-populated with vauth/vmutation/vread defaults; consumers
    /// can replace any entry or add new policies. The middleware looks up the policy named in
    /// each endpoint's <see cref="VRateLimitAttribute"/>.
    /// </summary>
    public Dictionary<string, RateLimitPolicy> Policies { get; } = new()
    {
        [VAppCoreRateLimitPolicies.Auth] = VAppCoreRateLimitPolicies.DefaultAuth,
        [VAppCoreRateLimitPolicies.Mutation] = VAppCoreRateLimitPolicies.DefaultMutation,
        [VAppCoreRateLimitPolicies.Read] = VAppCoreRateLimitPolicies.DefaultRead,
    };

    /// <summary>
    /// Per-role multipliers applied to capacity AND refill rate. Example:
    /// <c>{ "paid": 10, "admin": double.MaxValue }</c>. Roles read from <see cref="ICurrentUser.IsInRole"/>.
    /// User gets the highest multiplier their roles match. Default 1x for everyone.
    /// </summary>
    public Dictionary<string, double> TierMultipliers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>If true, registers <see cref="LoggingRateLimitObserver"/> automatically.</summary>
    public bool LogRejections { get; set; } = false;

    /// <summary>The default <see cref="MemoryStoreSweepInterval"/>: a minute.</summary>
    public static readonly TimeSpan DefaultMemoryStoreSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often, at most, <see cref="MemoryRateLimitStore"/> evicts the buckets that have refilled to capacity — no
    /// different from new ones — on the first request after the interval passes. Longer keeps more idle buckets in
    /// memory; shorter sweeps more often. Must be positive. The Redis store expires its keys on its own.
    /// </summary>
    public TimeSpan MemoryStoreSweepInterval { get; set; } = DefaultMemoryStoreSweepInterval;
}
