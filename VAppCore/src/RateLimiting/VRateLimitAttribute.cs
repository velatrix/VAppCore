namespace VAppCore;

/// <summary>
/// Apply to a controller, action, or minimal-API endpoint to rate-limit requests against the named policy.
/// <c>Cost</c> defaults to 1 — set higher for expensive endpoints sharing a token-bucket policy.
/// Endpoints without this attribute are NOT rate-limited.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class VRateLimitAttribute : Attribute
{
    public string PolicyName { get; }
    public int Cost { get; set; } = 1;

    public VRateLimitAttribute(string policyName)
    {
        PolicyName = policyName;
    }
}
