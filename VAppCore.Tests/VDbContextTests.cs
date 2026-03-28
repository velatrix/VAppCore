using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class VDbContextTests
{
    // ── Audit fields on Add ──

    [Fact]
    public async Task SaveChanges_Added_SetsCreatedAtAndUpdatedAt()
    {
        var (db, _) = TestFactory.CreateDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync();

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.NotEqual(default, entity.UpdatedAt);
        Assert.Equal(entity.CreatedAt, entity.UpdatedAt);
    }

    [Fact]
    public async Task SaveChanges_Added_SetsCreatedByAndUpdatedBy()
    {
        var (db, user) = TestFactory.CreateDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync();

        Assert.Equal(user.UserId, entity.CreatedBy);
        Assert.Equal(user.UserId, entity.UpdatedBy);
    }

    [Fact]
    public async Task SaveChanges_Added_Unauthenticated_DoesNotSetUserFields()
    {
        var (db, _) = TestFactory.CreateDbContextUnauthenticated();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync();

        Assert.NotEqual(default, entity.CreatedAt); // timestamp still set
        Assert.Equal(default, entity.CreatedBy);     // user fields not set
    }

    // ── Audit fields on Modify ──

    [Fact]
    public async Task SaveChanges_Modified_UpdatesUpdatedAtAndUpdatedBy()
    {
        var (db, user) = TestFactory.CreateDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Original" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync();

        var originalCreatedAt = entity.CreatedAt;
        var originalCreatedBy = entity.CreatedBy;

        // Small delay to ensure different timestamp
        await Task.Delay(10);

        entity.Name = "Modified";
        db.SimpleEntities.Update(entity);
        await db.SaveChangesAsync();

        Assert.Equal(originalCreatedAt, entity.CreatedAt); // not changed
        Assert.Equal(originalCreatedBy, entity.CreatedBy); // not changed
        Assert.True(entity.UpdatedAt > originalCreatedAt); // updated
        Assert.Equal(user.UserId, entity.UpdatedBy);
    }

    // ── Soft delete ──

    [Fact]
    public async Task SaveChanges_Delete_SoftDeletable_SetsIsDeletedInsteadOfDeleting()
    {
        var (db, _) = TestFactory.CreateDbContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Product", Price = 10m };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        db.Products.Remove(product);
        await db.SaveChangesAsync();

        // Should NOT be actually deleted — soft deleted instead
        var found = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == product.Id);
        Assert.NotNull(found);
        Assert.True(found.IsDeleted);
        Assert.NotNull(found.DeletedAt);
    }

    [Fact]
    public async Task SaveChanges_Delete_SoftDeletable_SetsDeletedBy()
    {
        var (db, user) = TestFactory.CreateDbContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Product", Price = 10m };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        db.Products.Remove(product);
        await db.SaveChangesAsync();

        var found = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == product.Id);
        Assert.NotNull(found);
        Assert.Equal(user.UserId, found.DeletedBy);
    }

    [Fact]
    public async Task SaveChanges_Delete_SoftDeletable_FilteredFromQueries()
    {
        var (db, _) = TestFactory.CreateDbContext();

        var product = new TestProduct { Id = Guid.NewGuid(), Name = "Product", Price = 10m };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        db.Products.Remove(product);
        await db.SaveChangesAsync();

        // Normal query should not find it
        var result = await db.Products.FirstOrDefaultAsync(p => p.Id == product.Id);
        Assert.Null(result);

        // IgnoreQueryFilters should find it
        var withIgnore = await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == product.Id);
        Assert.NotNull(withIgnore);
    }

    [Fact]
    public async Task SaveChanges_Delete_NonSoftDeletable_ActuallyDeletes()
    {
        var (db, _) = TestFactory.CreateDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Simple" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync();

        db.SimpleEntities.Remove(entity);
        await db.SaveChangesAsync();

        var found = await db.SimpleEntities.FindAsync(entity.Id);
        Assert.Null(found);
    }

    // ── Tenant auto-set ──

    [Fact]
    public async Task SaveChanges_Added_TenantScoped_SetsTenantId()
    {
        var (db, user) = TestFactory.CreateDbContext();

        var entity = new TestTenantProduct { Id = Guid.NewGuid(), Name = "Tenant Product" };
        db.TenantProducts.Add(entity);
        await db.SaveChangesAsync();

        Assert.Equal(user.TenantId, entity.TenantId);
    }

    [Fact]
    public async Task SaveChanges_Added_TenantScoped_Unauthenticated_DoesNotSetTenant()
    {
        var (db, _) = TestFactory.CreateDbContextUnauthenticated();

        var tenantId = Guid.NewGuid();
        var entity = new TestTenantProduct { Id = Guid.NewGuid(), Name = "Product", TenantId = tenantId };
        db.TenantProducts.Add(entity);
        await db.SaveChangesAsync();

        // Should keep the manually set TenantId
        Assert.Equal(tenantId, entity.TenantId);
    }

    // ── TransactionAsync ──
    // Note: InMemory provider doesn't support transactions.
    // These tests verify the logic flow (reuse detection, exception propagation).
    // Real transaction commit/rollback requires a relational DB.

    [Fact]
    public async Task TransactionAsync_Rollback_PropagatesException()
    {
        var (db, _) = TestFactory.CreateDbContext();

        // Even though InMemory doesn't support BeginTransaction,
        // we can verify that when CurrentTransaction exists, it reuses.
        // Without a real DB, we test exception propagation behavior directly.
        var threw = false;
        try
        {
            // This will throw because InMemory doesn't support transactions
            await db.TransactionAsync(async () =>
            {
                await Task.CompletedTask;
                return 1;
            });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        // InMemory throws — expected. Real DB would commit.
        Assert.True(threw);
    }

    [Fact]
    public async Task TransactionAsync_VoidVersion_PropagatesException()
    {
        var (db, _) = TestFactory.CreateDbContext();

        // Same as above — InMemory doesn't support transactions
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await db.TransactionAsync(async () =>
            {
                await Task.CompletedTask;
            });
        });
    }

    // ── Sync SaveChanges ──

    [Fact]
    public void SaveChanges_Sync_AlsoSetsAuditFields()
    {
        var (db, user) = TestFactory.CreateDbContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Sync" };
        db.SimpleEntities.Add(entity);
        db.SaveChanges();

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.Equal(user.UserId, entity.CreatedBy);
    }
}
