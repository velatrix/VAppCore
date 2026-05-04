using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ConcurrencyRegistrationTests
{
    [Fact]
    public void AddVAppCoreConcurrency_RegistersInterceptor_AndDefaultObserver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();
        services.AddVAppCoreConcurrency();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<ConcurrencyConflictInterceptor>());

        var observers = scope.ServiceProvider.GetServices<IConcurrencyConflictObserver>().ToList();
        Assert.Single(observers);
        Assert.IsType<NoOpConcurrencyConflictObserver>(observers[0]);
    }

    [Fact]
    public void AddVAppCoreConcurrency_LogConflictsTrue_RegistersLoggingObserver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();
        services.AddVAppCoreConcurrency(o => o.LogConflicts = true);

        var sp = services.BuildServiceProvider();
        var observers = sp.GetServices<IConcurrencyConflictObserver>().ToList();

        Assert.Contains(observers, o => o is LoggingConcurrencyConflictObserver);
    }

    [Fact]
    public void AddVAppCoreConcurrency_CustomObserverViaDI_IsAlsoRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();

        // User registers their own observer (e.g. for Prometheus metrics)
        services.AddSingleton<IConcurrencyConflictObserver, FakeMetricsObserver>();
        services.AddVAppCoreConcurrency();

        var sp = services.BuildServiceProvider();
        var observers = sp.GetServices<IConcurrencyConflictObserver>().ToList();
        Assert.Contains(observers, o => o is FakeMetricsObserver);
        Assert.Contains(observers, o => o is NoOpConcurrencyConflictObserver);
    }

    private class FakeMetricsObserver : IConcurrencyConflictObserver
    {
        public void OnConflict(ConcurrencyConflictDetails details) { }
    }
}
