using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class OutboxInterceptorTests
{
    private record TestEvent(string Note) : IDomainEvent;
    private record AnotherEvent(int Number) : IDomainEvent;

    private static (VanillaDbContext Db, TestCurrentUser User) CreateContext()
    {
        var user = new TestCurrentUser();
        var auditInterceptor = new VAuditInterceptor<Guid, Guid>(user);
        var outboxInterceptor = new OutboxInterceptor();

        var options = new DbContextOptionsBuilder<VanillaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(auditInterceptor, outboxInterceptor)
            .Options;

        return (new VanillaDbContext(options), user);
    }

    [Fact]
    public async Task Save_EntityWithoutEvents_AddsNoOutboxRows()
    {
        var (db, _) = CreateContext();
        db.SimpleEntities.Add(new TestSimpleEntity { Id = Guid.NewGuid(), Name = "no events" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await db.OutboxMessages.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_EntityWithOneEvent_WritesOneOutboxRow()
    {
        var (db, _) = CreateContext();
        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "x" };
        entity.RaiseEvent(new TestEvent("hello"));
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.OutboxMessages.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(rows);
        Assert.Equal(OutboxStatus.Pending, rows[0].Status);
        Assert.Contains(nameof(TestEvent), rows[0].Type);
        Assert.Contains("hello", rows[0].Payload);
    }

    [Fact]
    public async Task Save_MultipleEventsAcrossMultipleEntities_WritesAllOutboxRows()
    {
        var (db, _) = CreateContext();
        var e1 = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "e1" };
        e1.RaiseEvent(new TestEvent("a"));
        e1.RaiseEvent(new AnotherEvent(42));

        var e2 = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "e2" };
        e2.RaiseEvent(new TestEvent("b"));

        db.SimpleEntities.Add(e1);
        db.SimpleEntities.Add(e2);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.OutboxMessages.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(OutboxStatus.Pending, r.Status));
    }

    [Fact]
    public async Task Save_ClearsEventsFromEntity_AfterExtraction()
    {
        var (db, _) = CreateContext();
        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "x" };
        entity.RaiseEvent(new TestEvent("once"));
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entity.DomainEvents); // cleared after extraction

        // Saving again should NOT re-emit the same event
        entity.Name = "modified";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.OutboxMessages.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(rows); // still only the one from the first save
    }

    [Fact]
    public async Task Save_PayloadIsValidJson_AndDeserializesBackToEvent()
    {
        var (db, _) = CreateContext();
        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "x" };
        entity.RaiseEvent(new TestEvent("roundtrip"));
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        var eventType = Type.GetType(row.Type);
        Assert.NotNull(eventType);
        var deserialized = (TestEvent)System.Text.Json.JsonSerializer.Deserialize(row.Payload, eventType!)!;
        Assert.Equal("roundtrip", deserialized.Note);
    }
}
