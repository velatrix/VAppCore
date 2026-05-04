namespace VAppCore;

/// <summary>
/// A named rate-limit policy modeled as a token bucket.
/// <c>Capacity</c> = max tokens (max burst). <c>RefillTokensPerSecond</c> = sustained rate.
/// Example: 60 requests/minute with burst of 60 = Capacity=60, RefillTokensPerSecond=1.0.
/// </summary>
public record RateLimitPolicy(string Name, int Capacity, double RefillTokensPerSecond);

/// <summary>Result of attempting to consume tokens from a bucket.</summary>
public record RateLimitResult(bool Permitted, TimeSpan? RetryAfter);

/// <summary>
/// Default policy presets. Override via <c>AddVAppCoreRateLimiting(o =&gt; ...)</c>.
/// </summary>
public static class VAppCoreRateLimitPolicies
{
    public const string Auth = "vauth";
    public const string Mutation = "vmutation";
    public const string Read = "vread";

    /// <summary>5 attempts/min — for login/register/forgot-password.</summary>
    public static RateLimitPolicy DefaultAuth => new(Auth, Capacity: 5, RefillTokensPerSecond: 5.0 / 60);

    /// <summary>60 requests/min — for normal mutation endpoints (POST/PUT/DELETE).</summary>
    public static RateLimitPolicy DefaultMutation => new(Mutation, Capacity: 60, RefillTokensPerSecond: 60.0 / 60);

    /// <summary>300 requests/min — for read endpoints (GET).</summary>
    public static RateLimitPolicy DefaultRead => new(Read, Capacity: 300, RefillTokensPerSecond: 300.0 / 60);
}
