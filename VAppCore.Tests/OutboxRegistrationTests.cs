using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class OutboxRegistrationTests
{
    public record SampleEvent(int N) : IDomainEvent;
    public class SampleHandlerA : IDomainEventHandler<SampleEvent>
    {
        public Task Handle(SampleEvent evt, EventContext context, CancellationToken ct) => Task.CompletedTask;
    }
    public class SampleHandlerB : IDomainEventHandler<SampleEvent>
    {
        public Task Handle(SampleEvent evt, EventContext context, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void AddVAppCoreOutbox_RegistersInterceptor_Processor_Options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();
        services.AddVAppCoreOutbox<VanillaDbContext>();

        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<OutboxInterceptor>());
        var hostedServices = sp.GetServices<IHostedService>().ToList();
        Assert.Contains(hostedServices, h => h is OutboxProcessor);
    }

    [Fact]
    public void AddVAppCoreOutbox_AppliesOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<VanillaDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVAppCore<VanillaDbContext, Guid, Guid>();
        services.AddVAppCoreOutbox<VanillaDbContext>(o =>
        {
            o.PollInterval = TimeSpan.FromSeconds(7);
            o.MaxAttempts = 42;
        });

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(7), opts.PollInterval);
        Assert.Equal(42, opts.MaxAttempts);
    }

    [Fact]
    public void AddDomainEventHandlers_RegistersAllHandlersInAssembly()
    {
        var services = new ServiceCollection();
        services.AddDomainEventHandlers(Assembly.GetExecutingAssembly());

        var sp = services.BuildServiceProvider();
        var handlers = sp.GetServices<IDomainEventHandler<SampleEvent>>().ToList();

        Assert.Contains(handlers, h => h is SampleHandlerA);
        Assert.Contains(handlers, h => h is SampleHandlerB);
    }
}
