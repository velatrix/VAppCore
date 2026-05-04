using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

/// <summary>
/// Proves VService works with a plain DbContext (not VDbContext-derived) — the v2.0
/// decoupling goal. Db is now a plain DbContext, CurrentUser is injected directly.
/// </summary>
public class VServiceWithPlainDbContextTests
{
    private class PlainProductService : VService<TestProduct, Guid, Guid, Guid>
    {
        public ICurrentUser<Guid, Guid> CurrentUserExposed => CurrentUser;

        public async Task<TestProduct> Create(string name, decimal price)
        {
            var product = new TestProduct { Id = Guid.NewGuid(), Name = name, Price = price };
            Set.Add(product);
            await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return product;
        }
    }

    [Fact]
    public async Task VService_WorksWithPlainDbContext()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();
        var svc = new PlainProductService { Db = db, CurrentUser = user };

        var product = await svc.Create("Test", 99m);

        var found = await svc.GetByIdAsync(product.Id);
        Assert.Equal("Test", found.Name);
    }

    [Fact]
    public async Task VService_CurrentUserAccessible()
    {
        var (db, user) = TestFactory.CreateVanillaDbContext();
        var svc = new PlainProductService { Db = db, CurrentUser = user };

        Assert.Equal(user.UserId, svc.CurrentUserExposed.UserId);
    }
}
