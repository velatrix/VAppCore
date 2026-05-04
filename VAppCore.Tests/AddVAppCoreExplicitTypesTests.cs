using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

/// <summary>
/// AddVAppCore in v2 takes the user/tenant key types explicitly and does NOT
/// require TDbContext to inherit VDbContext. Registers ICurrentUser, the
/// DbContext alias, and the MVC config — that's it.
/// </summary>
public class AddVAppCoreExplicitTypesTests
{
    [Fact]
    public void AddVAppCore_RegistersCurrentUser_ForArbitraryDbContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var currentUser = scope.ServiceProvider.GetService<ICurrentUser<Guid, Guid>>();
        Assert.NotNull(currentUser);
    }

    [Fact]
    public void AddVAppCore_RegistersDbContextAlias_ForArbitraryDbContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        var db = scope.ServiceProvider.GetService<DbContext>();
        Assert.NotNull(db);
        Assert.IsType<VanillaDbContext>(db);
    }

    [Fact]
    public void AddVAppCore_DoesNotRequireVDbContextInheritance()
    {
        // VanillaDbContext inherits plain DbContext, not VDbContext.
        // Registration must succeed without throwing.
        var services = new ServiceCollection();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp);
    }
}
