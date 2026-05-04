using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class OutboxProcessorTests
{
    public record CountedEvent(int Number) : IDomainEvent;
    public record AlwaysFailEvent() : IDomainEvent;

    public class CountingHandler : IDomainEventHandler<CountedEvent>
    {
        public List<(int Number, EventContext Ctx)> Calls { get; } = [];
        public Task Handle(CountedEvent evt, EventContext context, CancellationToken ct)
        {
            Calls.Add((evt.Number, context));
            return Task.CompletedTask;
        }
    }

    public class AlwaysThrowsHandler : IDomainEventHandler<AlwaysFailEvent>
    {
        public int CallCount { get; private set; }
        public Task Handle(AlwaysFailEvent evt, EventContext context, CancellationToken ct)
        {
            CallCount++;
            throw new InvalidOperationException("simulated handler failure");
        }
    }

    private static (VanillaDbContext Db, IServiceProvider Sp, T Handler) BuildContextWithHandler<T>(T handlerInstance)
        where T : class
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();

        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        // Register the handler instance under the closed generic interface
        var handlerInterface = typeof(T).GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>));
        services.AddSingleton(handlerInterface, handlerInstance);

        return (db, services.BuildServiceProvider(), handlerInstance);
    }

    private static OutboxMessage SeedMessage<TEvent>(VanillaDbContext db, TEvent evt) where TEvent : IDomainEvent
    {
        var msg = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = evt.GetType().AssemblyQualifiedName!,
            Payload = JsonSerializer.Serialize(evt, evt.GetType()),
            Status = OutboxStatus.Pending,
            Attempts = 0
        };
        db.OutboxMessages.Add(msg);
        db.SaveChanges();
        return msg;
    }

    private static OutboxProcessor MakeProcessor(IServiceProvider sp, OutboxOptions? opts = null)
    {
        var scopeFactory = sp.GetService<IServiceScopeFactory>() ?? new TestScopeFactory(sp);
        return new OutboxProcessor(scopeFactory, Options.Create(opts ?? new OutboxOptions()), NullLogger<OutboxProcessor>.Instance);
    }

    // Pending → Sent on successful dispatch
    [Fact]
    public async Task ProcessOnce_PendingRowDispatched_MarkedSent_AndHandlerCalled()
    {
        var handler = new CountingHandler();
        var (db, sp, _) = BuildContextWithHandler(handler);
        SeedMessage(db, new CountedEvent(42));

        var processor = MakeProcessor(sp);
        await processor.ProcessOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Calls);
        Assert.Equal(42, handler.Calls[0].Number);

        var rows = await db.OutboxMessages.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxStatus.Sent, rows[0].Status);
        Assert.NotNull(rows[0].SentAt);
    }

    // EventContext carries MessageId / Attempt / OccurredAt
    [Fact]
    public async Task ProcessOnce_HandlerReceivesContextWithCorrectMessageIdAndAttempt()
    {
        var handler = new CountingHandler();
        var (db, sp, _) = BuildContextWithHandler(handler);
        var seeded = SeedMessage(db, new CountedEvent(1));

        var processor = MakeProcessor(sp);
        await processor.ProcessOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(seeded.Id, handler.Calls[0].Ctx.MessageId);
        Assert.Equal(1, handler.Calls[0].Ctx.Attempt);
    }

    // Failure → Pending stays, attempts++, NextRetryAt set
    [Fact]
    public async Task ProcessOnce_HandlerThrows_RowStaysPending_AttemptsIncremented_NextRetryScheduled()
    {
        var handler = new AlwaysThrowsHandler();
        var (db, sp, _) = BuildContextWithHandler(handler);
        SeedMessage(db, new AlwaysFailEvent());

        var processor = MakeProcessor(sp, new OutboxOptions { MaxAttempts = 5 });
        await processor.ProcessOnceAsync(TestContext.Current.CancellationToken);

        var row = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.NextRetryAt);
        Assert.NotNull(row.LastError);
        Assert.Contains("simulated", row.LastError);
    }

    // Multiple failures → DeadLettered after MaxAttempts
    [Fact]
    public async Task ProcessOnce_AfterMaxAttempts_RowMovedToDeadLettered()
    {
        var handler = new AlwaysThrowsHandler();
        var (db, sp, _) = BuildContextWithHandler(handler);
        SeedMessage(db, new AlwaysFailEvent());

        var opts = new OutboxOptions { MaxAttempts = 3 };
        var processor = MakeProcessor(sp, opts);

        // Force NextRetryAt to past so each pass picks it up
        for (int i = 0; i < 3; i++)
        {
            // Reset NextRetryAt to now so it's eligible
            var row = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
            row.NextRetryAt = null;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            await processor.ProcessOnceAsync(TestContext.Current.CancellationToken);
        }

        var final = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxStatus.DeadLettered, final.Status);
        Assert.Equal(3, final.Attempts);
        Assert.Equal(3, handler.CallCount);
    }

    // NextRetryAt in future → row not picked up yet
    [Fact]
    public async Task ProcessOnce_NextRetryAtInFuture_RowNotProcessed()
    {
        var handler = new CountingHandler();
        var (db, sp, _) = BuildContextWithHandler(handler);
        var msg = SeedMessage(db, new CountedEvent(1));
        msg.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var processor = MakeProcessor(sp);
        await processor.ProcessOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Calls);
        var row = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
    }

    // Pruning: Sent rows older than retention are deleted
    [Fact]
    public async Task PruneAsync_DeletesSentRowsOlderThanRetention_LeavesRecentAndPending()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();

        var oldSent = new OutboxMessage
        {
            Id = Guid.NewGuid(), Type = "x", Payload = "{}",
            Status = OutboxStatus.Sent, SentAt = DateTimeOffset.UtcNow.AddDays(-31)
        };
        var recentSent = new OutboxMessage
        {
            Id = Guid.NewGuid(), Type = "x", Payload = "{}",
            Status = OutboxStatus.Sent, SentAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var pendingOld = new OutboxMessage
        {
            Id = Guid.NewGuid(), Type = "x", Payload = "{}",
            Status = OutboxStatus.Pending
        };
        db.OutboxMessages.AddRange(oldSent, recentSent, pendingOld);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var sp = new ServiceCollection().AddSingleton<DbContext>(db).BuildServiceProvider();
        var processor = MakeProcessor(sp, new OutboxOptions { RetentionDays = 30 });

        await processor.PruneAsync(TestContext.Current.CancellationToken);

        var remaining = await db.OutboxMessages.Select(m => m.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(oldSent.Id, remaining);
        Assert.Contains(recentSent.Id, remaining);
        Assert.Contains(pendingOld.Id, remaining);
    }

    private class TestScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _sp;
        public TestScopeFactory(IServiceProvider sp) => _sp = sp;
        public IServiceScope CreateScope() => new TestScope(_sp);

        private class TestScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; }
            public TestScope(IServiceProvider sp) => ServiceProvider = sp;
            public void Dispose() { }
        }
    }
}
