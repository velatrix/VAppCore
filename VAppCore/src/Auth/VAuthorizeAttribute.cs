namespace VAppCore;

/// <summary>
/// Permission and role-based authorization attribute.
/// Apply to controllers or actions. Stackable — multiple attributes require ALL conditions.
/// Empty attribute = just requires authentication.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public class VAuthorizeAttribute : Attribute
{
    public string? Permission { get; set; }
    public string? Role { get; set; }

    /// <summary>
    /// When set, requires the caller is authenticated via the API key scheme AND the API key
    /// holds this permission. User cookies / JWTs are explicitly REJECTED on endpoints with
    /// <see cref="ApiKey"/> set, even if the user has the same permission.
    /// </summary>
    public string? ApiKey { get; set; }
}
