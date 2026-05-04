using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ApplyVAppCoreFiltersTests
{
    // ── Soft delete filter ──

    [Fact]
    public async Task SoftDeleteFilter_ExcludesDeletedRowsFromQueries()
    {
        var (db, _) = CreateFilteredVanillaContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Live", Price = 10m };
        var deleted = new TestProduct { Id = Guid.NewGuid(), Name = "Gone", Price = 20m, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow };
        db.Products.Add(product);
        db.Products.Add(deleted);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var liveItems = await db.Products.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(liveItems);
        Assert.Equal(product.Id, liveItems[0].Id);
    }

    [Fact]
    public async Task SoftDeleteFilter_IgnoreQueryFilters_SeesDeletedRows()
    {
        var (db, _) = CreateFilteredVanillaContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Live", Price = 10m };
        var deleted = new TestProduct { Id = Guid.NewGuid(), Name = "Gone", Price = 20m, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow };
        db.Products.Add(product);
        db.Products.Add(deleted);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var allItems = await db.Products.IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, allItems.Count);
    }

    // ── Tenant filter ──

    [Fact]
    public async Task TenantFilter_ScopesToCurrentTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (db, _) = CreateFilteredTenantedContext(tenantA);

        // Insert rows for tenantA and tenantB directly (bypass interceptor by setting TenantId manually)
        // Use IgnoreQueryFilters when adding because the filter doesn't apply at insert time anyway
        var aProduct = new TestTenantProduct { Id = Guid.NewGuid(), Name = "A", TenantId = tenantA };
        var bProduct = new TestTenantProduct { Id = Guid.NewGuid(), Name = "B", TenantId = tenantB };
        db.TenantProducts.Add(aProduct);
        db.TenantProducts.Add(bProduct);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Query under tenantA — should only see A's product
        var visible = await db.TenantProducts.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(visible);
        Assert.Equal(aProduct.Id, visible[0].Id);
    }

    [Fact]
    public async Task TenantFilter_NotApplied_WhenContextDoesNotImplementIVTenantContext()
    {
        // Use a vanilla context that does NOT implement IVTenantContext.
        // Tenant entities should be visible regardless of TenantId.
        var (db, _) = CreateFilteredVanillaContext();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        db.TenantProducts.Add(new TestTenantProduct { Id = Guid.NewGuid(), Name = "A", TenantId = tenantA });
        db.TenantProducts.Add(new TestTenantProduct { Id = Guid.NewGuid(), Name = "B", TenantId = tenantB });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var visible = await db.TenantProducts.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, visible.Count);
    }

    // ── Helpers ──

    private static (FilteredVanillaDbContext Db, TestCurrentUser User) CreateFilteredVanillaContext()
    {
        var user = new TestCurrentUser();
        var interceptor = new VAuditInterceptor<Guid, Guid>(user);

        var options = new DbContextOptionsBuilder<FilteredVanillaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        var db = new FilteredVanillaDbContext(options);
        return (db, user);
    }

    private static (FilteredTenantedDbContext Db, TestCurrentUser User) CreateFilteredTenantedContext(Guid currentTenantId)
    {
        var user = new TestCurrentUser { TenantId = currentTenantId };
        var interceptor = new VAuditInterceptor<Guid, Guid>(user);

        var options = new DbContextOptionsBuilder<FilteredTenantedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        var db = new FilteredTenantedDbContext(options, currentTenantId);
        return (db, user);
    }
}

// ── Test contexts that opt into ApplyVAppCoreFilters ──

internal class FilteredVanillaDbContext : DbContext
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();

    public FilteredVanillaDbContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyVAppCoreFilters<Guid, Guid>(this);
    }
}

internal class FilteredTenantedDbContext : DbContext, IVTenantContext<Guid>
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();

    public Guid CurrentTenantId { get; }

    public FilteredTenantedDbContext(DbContextOptions options, Guid currentTenantId) : base(options)
    {
        CurrentTenantId = currentTenantId;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyVAppCoreFilters<Guid, Guid>(this);
    }
}
