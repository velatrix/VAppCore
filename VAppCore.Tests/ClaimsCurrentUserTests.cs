using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VAppCore.Tests;

public class ClaimsCurrentUserTests
{
    private static ClaimsCurrentUser<Guid, Guid> CreateUser(
        IEnumerable<Claim>? claims = null,
        bool authenticated = true,
        VAppCoreAuthOptions? options = null,
        IPermissionResolver<Guid>? resolver = null)
    {
        var identity = authenticated
            ? new ClaimsIdentity(claims ?? [], "TestAuth")
            : new ClaimsIdentity(claims);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var opts = Options.Create(options ?? new VAppCoreAuthOptions());

        var services = new ServiceCollection();
        if (resolver != null)
            services.AddSingleton<IPermissionResolver<Guid>>(resolver);
        var sp = services.BuildServiceProvider();

        return new ClaimsCurrentUser<Guid, Guid>(accessor, opts, sp);
    }

    // ── IsAuthenticated ──

    [Fact]
    public void IsAuthenticated_True_WhenAuthenticated()
    {
        var user = CreateUser(authenticated: true);
        Assert.True(user.IsAuthenticated);
    }

    [Fact]
    public void IsAuthenticated_False_WhenNotAuthenticated()
    {
        var user = CreateUser(authenticated: false);
        Assert.False(user.IsAuthenticated);
    }

    // ── UserId ──

    [Fact]
    public void UserId_ParsesFromClaim()
    {
        var id = Guid.NewGuid();
        var user = CreateUser([new Claim("sub", id.ToString())]);

        Assert.Equal(id, user.UserId);
    }

    [Fact]
    public void UserId_DefaultsWhenMissing()
    {
        var user = CreateUser([]);
        Assert.Equal(default, user.UserId);
    }

    // ── TenantId ──

    [Fact]
    public void TenantId_ParsesFromClaim()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser([new Claim("tenant_id", tenantId.ToString())]);

        Assert.Equal(tenantId, user.TenantId);
    }

    [Fact]
    public void TenantId_UsesCustomClaimName()
    {
        var tenantId = Guid.NewGuid();
        var user = CreateUser(
            [new Claim("org_id", tenantId.ToString())],
            options: new VAppCoreAuthOptions { TenantIdClaim = "org_id" });

        Assert.Equal(tenantId, user.TenantId);
    }

    // ── Email ──

    [Fact]
    public void Email_ParsesFromClaim()
    {
        var user = CreateUser([new Claim("email", "test@example.com")]);
        Assert.Equal("test@example.com", user.Email);
    }

    [Fact]
    public void Email_NullWhenMissing()
    {
        var user = CreateUser([]);
        Assert.Null(user.Email);
    }

    // ── Roles from claims ──

    [Fact]
    public void Roles_ParsesFromClaims()
    {
        var user = CreateUser([
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(ClaimTypes.Role, "User")
        ]);

        Assert.Equal(2, user.Roles.Count);
        Assert.Contains("Admin", user.Roles);
        Assert.Contains("User", user.Roles);
    }

    [Fact]
    public void IsInRole_CaseInsensitive()
    {
        var user = CreateUser([new Claim(ClaimTypes.Role, "Admin")]);

        Assert.True(user.IsInRole("admin"));
        Assert.True(user.IsInRole("ADMIN"));
        Assert.False(user.IsInRole("Editor"));
    }

    // ── Permissions from claims ──

    [Fact]
    public void Permissions_ParsesFromClaims()
    {
        var user = CreateUser([
            new Claim("permission", "products.read"),
            new Claim("permission", "products.write")
        ]);

        Assert.Equal(2, user.Permissions.Count);
        Assert.Contains("products.read", user.Permissions);
    }

    [Fact]
    public void HasPermission_CaseInsensitive()
    {
        var user = CreateUser([new Claim("permission", "products.read")]);

        Assert.True(user.HasPermission("products.read"));
        Assert.True(user.HasPermission("Products.Read"));
        Assert.False(user.HasPermission("products.write"));
    }

    // ── Custom claim names ──

    [Fact]
    public void CustomClaimNames_Work()
    {
        var id = Guid.NewGuid();
        var user = CreateUser(
            [
                new Claim("user_id", id.ToString()),
                new Claim("custom_role", "SuperAdmin"),
                new Claim("scope", "read"),
                new Claim("scope", "write")
            ],
            options: new VAppCoreAuthOptions
            {
                UserIdClaim = "user_id",
                RoleClaim = "custom_role",
                PermissionClaim = "scope"
            });

        Assert.Equal(id, user.UserId);
        Assert.True(user.IsInRole("SuperAdmin"));
        Assert.True(user.HasPermission("read"));
        Assert.True(user.HasPermission("write"));
    }

    // ── IPermissionResolver ──

    [Fact]
    public void Roles_UsesResolver_WhenRegistered()
    {
        var resolver = new TestPermissionResolver
        {
            Roles = ["ResolvedAdmin", "ResolvedUser"]
        };

        var userId = Guid.NewGuid();
        var user = CreateUser(
            [
                new Claim("sub", userId.ToString()),
                new Claim(ClaimTypes.Role, "ClaimRole") // should be ignored
            ],
            resolver: resolver);

        Assert.Contains("ResolvedAdmin", user.Roles);
        Assert.DoesNotContain("ClaimRole", user.Roles);
    }

    [Fact]
    public void Permissions_UsesResolver_WhenRegistered()
    {
        var resolver = new TestPermissionResolver
        {
            Permissions = ["resolved.read", "resolved.write"]
        };

        var userId = Guid.NewGuid();
        var user = CreateUser(
            [
                new Claim("sub", userId.ToString()),
                new Claim("permission", "claim.perm") // should be ignored
            ],
            resolver: resolver);

        Assert.Contains("resolved.read", user.Permissions);
        Assert.DoesNotContain("claim.perm", user.Permissions);
    }

    // ── Caching ──

    [Fact]
    public void Roles_CachedPerInstance()
    {
        var resolver = new TestPermissionResolver { Roles = ["Admin"] };
        var userId = Guid.NewGuid();
        var user = CreateUser([new Claim("sub", userId.ToString())], resolver: resolver);

        _ = user.Roles;
        _ = user.Roles; // second access

        Assert.Equal(1, resolver.RoleCallCount); // only called once
    }

    private class TestPermissionResolver : IPermissionResolver<Guid>
    {
        public IReadOnlyList<string> Roles { get; set; } = [];
        public IReadOnlyList<string> Permissions { get; set; } = [];
        public int RoleCallCount { get; private set; }
        public int PermissionCallCount { get; private set; }

        public Task<IReadOnlyList<string>> GetRolesAsync(Guid userId)
        {
            RoleCallCount++;
            return Task.FromResult(Roles);
        }

        public Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId)
        {
            PermissionCallCount++;
            return Task.FromResult(Permissions);
        }
    }
}
