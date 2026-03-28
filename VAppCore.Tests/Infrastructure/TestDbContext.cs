using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore.Tests.Infrastructure;

// ── Test entities ──

public class TestProduct : VEntity<Guid, Guid, Guid>, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}

public class TestTenantProduct : VEntity<Guid, Guid, Guid>, ITenantScoped<Guid>
{
    public string Name { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
}

public class TestSimpleEntity : VEntity<Guid, Guid, Guid>
{
    public string Name { get; set; } = string.Empty;
}

// ── Test DbContext ──

public class TestDbContext : VDbContext<Guid, Guid, Guid>
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();

    public TestDbContext(DbContextOptions options, IServiceProvider sp) : base(options, sp) { }
}

// ── Test CurrentUser ──

public class TestCurrentUser : ICurrentUser<Guid, Guid>
{
    public bool IsAuthenticated { get; set; } = true;
    public Guid UserId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Guid.NewGuid();
    public string? Email { get; set; } = "test@test.com";
    public IReadOnlyList<string> Roles { get; set; } = [];
    public IReadOnlyList<string> Permissions { get; set; } = [];
    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

// ── Factory ──

public static class TestFactory
{
    public static (TestDbContext Db, TestCurrentUser User) CreateDbContext()
    {
        var user = new TestCurrentUser();

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        services.AddSingleton<ICurrentUser>(user);
        var sp = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new TestDbContext(options, sp);
        return (db, user);
    }

    public static (TestDbContext Db, TestCurrentUser User) CreateDbContextUnauthenticated()
    {
        var user = new TestCurrentUser { IsAuthenticated = false };

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        services.AddSingleton<ICurrentUser>(user);
        var sp = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new TestDbContext(options, sp);
        return (db, user);
    }
}
