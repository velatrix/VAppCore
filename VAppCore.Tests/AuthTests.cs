namespace VAppCore.Tests;

public class VAuthorizeAttributeTests
{
    [Fact]
    public void DefaultAttribute_NoPermissionOrRole()
    {
        var attr = new VAuthorizeAttribute();

        Assert.Null(attr.Permission);
        Assert.Null(attr.Role);
    }

    [Fact]
    public void Attribute_WithPermission()
    {
        var attr = new VAuthorizeAttribute { Permission = "products.read" };

        Assert.Equal("products.read", attr.Permission);
        Assert.Null(attr.Role);
    }

    [Fact]
    public void Attribute_WithRole()
    {
        var attr = new VAuthorizeAttribute { Role = "Admin" };

        Assert.Equal("Admin", attr.Role);
        Assert.Null(attr.Permission);
    }

    [Fact]
    public void Attribute_WithBoth()
    {
        var attr = new VAuthorizeAttribute { Permission = "products.delete", Role = "Admin" };

        Assert.Equal("products.delete", attr.Permission);
        Assert.Equal("Admin", attr.Role);
    }

    [Fact]
    public void Attribute_AllowsMultiple()
    {
        var attrs = typeof(TestController)
            .GetMethod(nameof(TestController.MultiAuth))!
            .GetCustomAttributes(typeof(VAuthorizeAttribute), false);

        Assert.Equal(2, attrs.Length);
    }

    [Fact]
    public void Attribute_CanBeOnClass()
    {
        var attrs = typeof(TestController)
            .GetCustomAttributes(typeof(VAuthorizeAttribute), false);

        Assert.Single(attrs);
    }

    [VAuthorize]
    private class TestController
    {
        [VAuthorize(Permission = "a")]
        [VAuthorize(Role = "Admin")]
        public void MultiAuth() { }
    }
}

public class VAppCoreAuthOptionsTests
{
    [Fact]
    public void DefaultOptions_HaveSensibleDefaults()
    {
        var options = new VAppCoreAuthOptions();

        Assert.Equal("sub", options.UserIdClaim);
        Assert.Equal("tenant_id", options.TenantIdClaim);
        Assert.Equal("permission", options.PermissionClaim);
        Assert.Equal("email", options.EmailClaim);
    }
}
