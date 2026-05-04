using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class VAuditInterceptorTests
{
    // ── Audit on Add ──

    [Fact]
    public async Task Interceptor_Added_SetsCreatedAtAndUpdatedAt()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.NotEqual(default, entity.UpdatedAt);
        Assert.Equal(entity.CreatedAt, entity.UpdatedAt);
    }

    [Fact]
    public async Task Interceptor_Added_SetsCreatedByAndUpdatedBy()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(user.UserId, entity.CreatedBy);
        Assert.Equal(user.UserId, entity.UpdatedBy);
    }

    [Fact]
    public async Task Interceptor_Added_Unauthenticated_DoesNotSetUserFields()
    {
        var (db, _) = TestFactory.CreateVanillaDbContextUnauthenticated();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(default, entity.CreatedAt); // timestamps still set
        Assert.Equal(default, entity.CreatedBy);     // user fields not set
    }

    // ── Audit on Modify ──

    [Fact]
    public async Task Interceptor_Modified_UpdatesUpdatedAtAndUpdatedBy()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Original" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var originalCreatedAt = entity.CreatedAt;
        var originalCreatedBy = entity.CreatedBy;

        await Task.Delay(10, TestContext.Current.CancellationToken);

        entity.Name = "Modified";
        db.SimpleEntities.Update(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(originalCreatedAt, entity.CreatedAt); // not changed
        Assert.Equal(originalCreatedBy, entity.CreatedBy); // not changed
        Assert.True(entity.UpdatedAt > originalCreatedAt); // updated
        Assert.Equal(user.UserId, entity.UpdatedBy);
    }

    // ── Soft delete ──

    [Fact]
    public async Task Interceptor_Delete_SoftDeletable_SetsIsDeletedInsteadOfDeleting()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Product", Price = 10m };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Products.Remove(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var found = await db.Products.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.True(found.IsDeleted);
        Assert.NotNull(found.DeletedAt);
    }

    [Fact]
    public async Task Interceptor_Delete_SoftDeletable_SetsDeletedBy()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Product", Price = 10m };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Products.Remove(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var found = await db.Products.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(found);
        Assert.Equal(user.UserId, found.DeletedBy);
    }

    [Fact]
    public async Task Interceptor_Delete_NonSoftDeletable_ActuallyDeletes()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Simple" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.SimpleEntities.Remove(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var found = await db.SimpleEntities.FindAsync([entity.Id], TestContext.Current.CancellationToken);
        Assert.Null(found);
    }

    // ── Tenant auto-set ──

    [Fact]
    public async Task Interceptor_Added_TenantScoped_SetsTenantId()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();

        var entity = new TestTenantProduct { Id = Guid.NewGuid(), Name = "Tenant Product" };
        db.TenantProducts.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(user.TenantId, entity.TenantId);
    }

    [Fact]
    public async Task Interceptor_Added_TenantScoped_Unauthenticated_DoesNotSetTenant()
    {
        var (db, _) = TestFactory.CreateVanillaDbContextUnauthenticated();

        var tenantId = Guid.NewGuid();
        var entity = new TestTenantProduct { Id = Guid.NewGuid(), Name = "Product", TenantId = tenantId };
        db.TenantProducts.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Should keep the manually set TenantId
        Assert.Equal(tenantId, entity.TenantId);
    }

    // ── Sync SaveChanges ──

    [Fact]
    public void Interceptor_Sync_AlsoSetsAuditFields()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Sync" };
        db.SimpleEntities.Add(entity);
        db.SaveChanges();

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.Equal(user.UserId, entity.CreatedBy);
    }
}
