using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

/// <summary>
/// Mirrors the Hub use-case: a DbContext that inherits a non-VAppCore base
/// (which itself has SaveChanges overrides and OnModelCreating logic) and
/// composes VAppCore via the interceptor + filter extension. Proves the
/// building blocks interoperate without VDbContext involvement.
/// </summary>
public class IdentityShapedIntegrationTests
{
    [Fact]
    public async Task IdentityShaped_AuditFieldsPopulate()
    {
        var (db, user) = CreateContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Test", Price = 1m };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(default, product.CreatedAt);
        Assert.Equal(user.UserId, product.CreatedBy);
    }

    [Fact]
    public async Task IdentityShaped_BaseSaveChangesStillRuns()
    {
        var (db, _) = CreateContext();
        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Test", Price = 1m };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The fake base context counts saves — proves we didn't bypass the base class.
        Assert.Equal(1, db.SaveCallCount);
    }

    [Fact]
    public async Task IdentityShaped_SoftDeleteFilterApplies()
    {
        var (db, _) = CreateContext();

        var live = new TestProduct { Id = Guid.NewGuid(), Name = "Live", Price = 1m };
        var dead = new TestProduct { Id = Guid.NewGuid(), Name = "Dead", Price = 1m, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow };
        db.Products.Add(live);
        db.Products.Add(dead);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var visible = await db.Products.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(visible);
    }

    [Fact]
    public async Task IdentityShaped_DeleteSoftDeletesViaInterceptor()
    {
        var (db, _) = CreateContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Test", Price = 1m };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Products.Remove(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var found = await db.Products.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.True(found.IsDeleted);
    }

    [Fact]
    public async Task IdentityShaped_TransactionExtensionWorks()
    {
        var (db, _) = CreateContext();

        // InMemory throws on BeginTransaction — the extension is responsible for
        // propagating that exception (same shape as VDbContext tests).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await db.TransactionAsync(async () =>
            {
                await Task.CompletedTask;
                return 1;
            });
        });
    }

    private static (HubLikeDbContext Db, TestCurrentUser User) CreateContext()
    {
        var user = new TestCurrentUser();
        var options = new DbContextOptionsBuilder<HubLikeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddVAppCoreInterceptors<HubLikeDbContext, Guid, Guid>(user)
            .Options;
        return (new HubLikeDbContext(options), user);
    }
}

// ── Fake "other base" context, mirroring IdentityDbContext's shape ──
//
// Inherits a non-VAppCore base class that has its own SaveChanges override
// and its own OnModelCreating logic. The Hub-shaped context inherits this
// and wires VAppCore on top via OnModelCreating + the AddVAppCoreInterceptors
// helper at the options level.

internal abstract class FakeIdentityDbContextBase : DbContext
{
    public int SaveCallCount { get; private set; }

    protected FakeIdentityDbContextBase(DbContextOptions options) : base(options) { }

    public override int SaveChanges()
    {
        SaveCallCount++;
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCallCount++;
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Mirror IdentityDbContext shape: tweak some entity config of our own.
        modelBuilder.Entity<TestSimpleEntity>(b => b.Property(e => e.Name).HasMaxLength(256));
    }
}

internal class HubLikeDbContext : FakeIdentityDbContextBase
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();

    public HubLikeDbContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyVAppCoreFilters<Guid, Guid>(this);
    }
}
