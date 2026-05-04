using System.Security.Claims;

namespace VAppCore.Tests;

public class UseAspNetIdentityPresetTests
{
    [Fact]
    public void Preset_OverridesClaimNamesToIdentityConventions()
    {
        var options = new VAppCoreOptions();

        options.UseAspNetIdentity();

        Assert.Equal(ClaimTypes.NameIdentifier, options.UserIdClaim);
        Assert.Equal(ClaimTypes.Role, options.RoleClaim);
        Assert.Equal(ClaimTypes.Email, options.EmailClaim);
    }

    [Fact]
    public void Preset_LeavesTenantAndPermissionClaimsUntouched()
    {
        var options = new VAppCoreOptions();
        var defaultTenant = options.TenantIdClaim;
        var defaultPermission = options.PermissionClaim;

        options.UseAspNetIdentity();

        Assert.Equal(defaultTenant, options.TenantIdClaim);
        Assert.Equal(defaultPermission, options.PermissionClaim);
    }

    [Fact]
    public void Preset_ReturnsTheOptionsForChaining()
    {
        var options = new VAppCoreOptions();
        var returned = options.UseAspNetIdentity();
        Assert.Same(options, returned);
    }
}
