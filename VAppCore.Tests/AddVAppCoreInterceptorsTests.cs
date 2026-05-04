using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class AddVAppCoreInterceptorsTests
{
    [Fact]
    public async Task Helper_FromServiceProvider_RegistersAuditInterceptor()
    {
        var user = new TestCurrentUser();
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        var sp = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<VanillaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddVAppCoreInterceptors<Guid, Guid>(sp)
            .Options;

        await using var db = new VanillaDbContext(options);

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.Equal(user.UserId, entity.CreatedBy);
    }

    [Fact]
    public async Task Helper_FromCurrentUser_RegistersAuditInterceptor()
    {
        var user = new TestCurrentUser();

        var options = new DbContextOptionsBuilder<VanillaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddVAppCoreInterceptors<Guid, Guid>(user)
            .Options;

        await using var db = new VanillaDbContext(options);

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.Equal(user.UserId, entity.CreatedBy);
    }
}
