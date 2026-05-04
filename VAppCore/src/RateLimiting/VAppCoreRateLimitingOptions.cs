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
}
