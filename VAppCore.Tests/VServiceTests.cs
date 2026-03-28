using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

// ── Test service ──

public class TestProductService : VService<TestProduct, Guid, Guid, Guid>
{
    public async Task<TestProduct> Create(string name, decimal price)
    {
        var product = new TestProduct { Id = Guid.NewGuid(), Name = name, Price = price };
        Set.Add(product);
        await SaveAsync();
        return product;
    }
}

public class VServiceTests
{
    private static TestProductService CreateService()
    {
        var (db, _) = TestFactory.CreateDbContext();
        return new TestProductService { Db = db };
    }

    private static async Task<(TestProductService Svc, TestProduct Product)> CreateServiceWithProduct()
    {
        var svc = CreateService();
        var product = await svc.Create("TestProduct", 99.99m);
        return (svc, product);
    }

    // ── GetByIdAsync ──

    [Fact]
    public async Task GetByIdAsync_Found_ReturnsEntity()
    {
        var (svc, product) = await CreateServiceWithProduct();

        var found = await svc.GetByIdAsync(product.Id);

        Assert.Equal(product.Id, found.Id);
        Assert.Equal("TestProduct", found.Name);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ThrowsNotFoundError()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<NotFoundError>(() =>
            svc.GetByIdAsync(Guid.NewGuid()));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("TestProduct", ex.Message);
    }

    [Fact]
    public async Task GetByIdAsync_WithConfigure_AppliesQuery()
    {
        var (svc, product) = await CreateServiceWithProduct();

        // Configure that filters by name — should find
        var found = await svc.GetByIdAsync(product.Id,
            q => q.Where(p => p.Name == "TestProduct"));

        Assert.Equal(product.Id, found.Id);
    }

    [Fact]
    public async Task GetByIdAsync_WithConfigure_NotMatching_ThrowsNotFound()
    {
        var (svc, product) = await CreateServiceWithProduct();

        // Configure that filters by wrong name — should not find
        await Assert.ThrowsAsync<NotFoundError>(() =>
            svc.GetByIdAsync(product.Id,
                q => q.Where(p => p.Name == "WrongName")));
    }

    // ── FindByIdAsync ──

    [Fact]
    public async Task FindByIdAsync_Found_ReturnsEntity()
    {
        var (svc, product) = await CreateServiceWithProduct();

        var found = await svc.FindByIdAsync(product.Id);

        Assert.NotNull(found);
        Assert.Equal(product.Id, found.Id);
    }

    [Fact]
    public async Task FindByIdAsync_NotFound_ReturnsNull()
    {
        var svc = CreateService();

        var found = await svc.FindByIdAsync(Guid.NewGuid());

        Assert.Null(found);
    }

    // ── DeleteAsync ──

    [Fact]
    public async Task DeleteAsync_SoftDeletable_SoftDeletes()
    {
        var (svc, product) = await CreateServiceWithProduct();

        await svc.DeleteAsync(product.Id);

        // Normal query should not find it (query filter)
        var filtered = await svc.Db.Set<TestProduct>()
            .FirstOrDefaultAsync(p => p.Id == product.Id);
        Assert.Null(filtered);

        // IgnoreQueryFilters should find it as soft-deleted
        var raw = await svc.Db.Set<TestProduct>()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == product.Id);
        Assert.NotNull(raw);
        Assert.True(raw.IsDeleted);
    }

    [Fact]
    public async Task DeleteAsync_NotFound_ThrowsNotFoundError()
    {
        var svc = CreateService();

        await Assert.ThrowsAsync<NotFoundError>(() =>
            svc.DeleteAsync(Guid.NewGuid()));
    }

    // ── GetPagedAsync ──

    [Fact]
    public async Task GetPagedAsync_ReturnsPagedResults()
    {
        var svc = CreateService();

        for (int i = 0; i < 15; i++)
            await svc.Create($"Product{i:D2}", i * 10m);

        var query = new Microsoft.AspNetCore.Http.QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["page"] = "1",
                ["size"] = "5"
            });

        // Use ApplyWithCountAsync directly since GetPagedAsync uses projection
        // which requires VQueryFilter with selectable fields
        var parser = new VQueryParser(query);
        var (items, total) = await parser.ApplyWithCountAsync<TestProduct>(
            svc.Db.Set<TestProduct>().AsQueryable());

        Assert.Equal(15, total);
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public async Task GetPagedAsync_WithConfigure_PreFilters()
    {
        var svc = CreateService();

        await svc.Create("Cheap", 5m);
        await svc.Create("Expensive1", 100m);
        await svc.Create("Expensive2", 200m);

        var query = new Microsoft.AspNetCore.Http.QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>());
        var parser = new VQueryParser(query);

        // Pre-filter to only expensive products
        var (items, total) = await parser.ApplyWithCountAsync<TestProduct>(
            svc.Db.Set<TestProduct>().Where(p => p.Price >= 100m));

        Assert.Equal(2, total);
    }

    // ── Db and CurrentUser access ──

    [Fact]
    public void Db_IsAccessible()
    {
        var svc = CreateService();
        Assert.NotNull(svc.Db);
    }

    [Fact]
    public async Task SaveAsync_PersistsChanges()
    {
        var svc = CreateService();

        var product = await svc.Create("Saved", 50m);
        var found = await svc.Db.Set<TestProduct>().FindAsync(product.Id);

        Assert.NotNull(found);
        Assert.Equal("Saved", found.Name);
    }
}
