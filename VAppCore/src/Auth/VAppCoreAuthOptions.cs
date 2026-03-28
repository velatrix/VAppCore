using System.Security.Claims;

namespace VAppCore;

public class VAppCoreAuthOptions
{
    public string UserIdClaim { get; set; } = "sub";
    public string TenantIdClaim { get; set; } = "tenant_id";
    public string RoleClaim { get; set; } = ClaimTypes.Role;
    public string PermissionClaim { get; set; } = "permission";
    public string EmailClaim { get; set; } = "email";
}
