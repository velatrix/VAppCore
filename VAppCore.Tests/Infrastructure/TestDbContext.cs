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

public class TestNullableEntity : VEntity<Guid, Guid, Guid>
{
    public string? OptionalName { get; set; }
    public int? OptionalScore { get; set; }
}

public class TestConcurrentEntity : VEntity<Guid, Guid, Guid>, IConcurrent
{
    public string Name { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}

public class TestConcurrentXminEntity : VEntity<Guid, Guid, Guid>, IConcurrentXmin
{
    public string Name { get; set; } = string.Empty;
    public uint Xmin { get; set; }
}

public class TestAuditedEntity : VEntity<Guid, Guid, Guid>, IAuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
}

public class TestAuditedSoftDeletable : VEntity<Guid, Guid, Guid>, IAuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}

public class TestAuditedWithSkippedField : VEntity<Guid, Guid, Guid>, IAuditedEntity
{
    public string Name { get; set; } = string.Empty;

    [NotAudited]
    public int LoginCount { get; set; }
}

// ── Test DbContext (plain DbContext — v2 wires VAppCore at options level) ──

public class TestDbContext : DbContext
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();
    public DbSet<TestNullableEntity> NullableEntities => Set<TestNullableEntity>();
    public DbSet<TestConcurrentEntity> ConcurrentEntities => Set<TestConcurrentEntity>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<TestAuditedEntity> AuditedEntities => Set<TestAuditedEntity>();
    public DbSet<TestAuditedSoftDeletable> AuditedSoftDeletables => Set<TestAuditedSoftDeletable>();
    public DbSet<TestAuditedWithSkippedField> AuditedWithSkipped => Set<TestAuditedWithSkippedField>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public TestDbContext(DbContextOptions options) : base(options) { }
}

// Vanilla alias — kept for tests that explicitly need to assert the "no VAppCore wiring" baseline.
public class VanillaDbContext : DbContext
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();
    public DbSet<TestNullableEntity> NullableEntities => Set<TestNullableEntity>();
    public DbSet<TestConcurrentEntity> ConcurrentEntities => Set<TestConcurrentEntity>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<TestAuditedEntity> AuditedEntities => Set<TestAuditedEntity>();
    public DbSet<TestAuditedSoftDeletable> AuditedSoftDeletables => Set<TestAuditedSoftDeletable>();
    public DbSet<TestAuditedWithSkippedField> AuditedWithSkipped => Set<TestAuditedWithSkippedField>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public VanillaDbContext(DbContextOptions options) : base(options) { }
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
        var sp = BuildServiceProvider(user);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseVAppCore<TestDbContext, Guid, Guid>(sp)
            .Options;

        return (new TestDbContext(options), user);
    }

    public static (TestDbContext Db, TestCurrentUser User) CreateDbContextUnauthenticated()
    {
        var user = new TestCurrentUser { IsAuthenticated = false };
        var sp = BuildServiceProvider(user);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseVAppCore<TestDbContext, Guid, Guid>(sp)
            .Options;

        return (new TestDbContext(options), user);
    }

    public static (VanillaDbContext Db, TestCurrentUser User) CreateVanillaDbContext()
    {
        var user = new TestCurrentUser();
        var interceptor = new VAuditInterceptor<Guid, Guid>(user);

        var options = new DbContextOptionsBuilder<VanillaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        var db = new VanillaDbContext(options);
        return (db, user);
    }

    public static (VanillaDbContext Db, TestCurrentUser User) CreateVanillaDbContextUnauthenticated()
    {
        var user = new TestCurrentUser { IsAuthenticated = false };
        var interceptor = new VAuditInterceptor<Guid, Guid>(user);

        var options = new DbContextOptionsBuilder<VanillaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        var db = new VanillaDbContext(options);
        return (db, user);
    }

    public static (TestDbContext Db, TestCurrentUser User) CreateAuditLogDbContext(bool authenticated = true)
    {
        var user = new TestCurrentUser { IsAuthenticated = authenticated };

        var auditInterceptor = new AuditLogInterceptor<Guid, Guid>(authenticated ? user : null);
        var vAuditInterceptor = new VAuditInterceptor<Guid, Guid>(authenticated ? user : null);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // VAuditInterceptor registered FIRST: it transforms Deleted→Modified for ISoftDeletable
            // before AuditLogInterceptor sees the entry. AuditLogInterceptor still works either way
            // (it detects soft delete by IsDeleted false→true), but this matches production wiring.
            .AddInterceptors(vAuditInterceptor, auditInterceptor)
            .Options;

        return (new TestDbContext(options), user);
    }

    private static IServiceProvider BuildServiceProvider(TestCurrentUser user)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        services.AddSingleton<ICurrentUser>(user);
        return services.BuildServiceProvider();
    }
}
