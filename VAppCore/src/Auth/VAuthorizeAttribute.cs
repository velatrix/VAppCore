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
}
