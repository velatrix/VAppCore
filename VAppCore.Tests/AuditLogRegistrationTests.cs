using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class AuditLogRegistrationTests
{
    [Fact]
    public void AddVAppCoreAuditLog_RegistersInterceptorAndAuditLogService()
    {
        var services = new ServiceCollection();
        var user = new TestCurrentUser();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        services.AddDbContext<TestDbContext>(opts =>
            opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddVAppCoreAuditLog<TestDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<AuditLogInterceptor<Guid, Guid>>());
        Assert.IsType<AuditLogService>(scope.ServiceProvider.GetService<IAuditLog>());
    }

    [Fact]
    public async Task AddVAppCoreAuditInterceptors_WiresInterceptorToDbContext()
    {
        var services = new ServiceCollection();
        var user = new TestCurrentUser();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        services.AddSingleton<ICurrentUser>(user);

        services.AddDbContext<TestDbContext>((sp, opts) =>
        {
            opts.UseInMemoryDatabase(Guid.NewGuid().ToString());
            opts.AddInterceptors(new VAuditInterceptor<Guid, Guid>(user));
            opts.AddVAppCoreAuditInterceptors<Guid, Guid>(sp);
        });
        services.AddVAppCoreAuditLog<TestDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "wired" };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddVAppCoreAuditLog_IAuditLogResolvesAndReadsHistory()
    {
        var services = new ServiceCollection();
        var user = new TestCurrentUser();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        services.AddSingleton<ICurrentUser>(user);

        services.AddDbContext<TestDbContext>((sp, opts) =>
        {
            opts.UseInMemoryDatabase(Guid.NewGuid().ToString());
            opts.AddInterceptors(new VAuditInterceptor<Guid, Guid>(user));
            opts.AddVAppCoreAuditInterceptors<Guid, Guid>(sp);
        });
        services.AddVAppCoreAuditLog<TestDbContext, Guid, Guid>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();

        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "via-DI" };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var history = await audit.GetHistoryAsync<TestAuditedEntity>(entity.Id, TestContext.Current.CancellationToken);
        Assert.Single(history);
    }
}
