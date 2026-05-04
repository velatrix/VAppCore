using System.Security.Claims;

namespace VAppCore;

public static class VAppCoreOptionsExtensions
{
    /// <summary>
    /// Pre-fills the claim names that ASP.NET Identity uses on its issued cookies:
    /// <see cref="ClaimTypes.NameIdentifier"/> for the user id,
    /// <see cref="ClaimTypes.Role"/> for roles, and <see cref="ClaimTypes.Email"/> for email.
    /// Tenant and permission claims are left at their defaults.
    /// </summary>
    /// <remarks>
    /// This adds NO package dependency on ASP.NET Identity — <see cref="ClaimTypes"/>
    /// is part of <c>System.Security.Claims</c> in the BCL. The preset is just
    /// a convenience for the most common Identity-shaped configuration.
    /// </remarks>
    public static VAppCoreOptions UseAspNetIdentity(this VAppCoreOptions options)
    {
        options.UserIdClaim = ClaimTypes.NameIdentifier;
        options.RoleClaim = ClaimTypes.Role;
        options.EmailClaim = ClaimTypes.Email;
        return options;
    }
}
