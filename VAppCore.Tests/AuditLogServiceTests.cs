using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class AuditLogServiceTests
{
    [Fact]
    public async Task GetHistoryAsync_ReturnsRowsForEntity_OrderedByChangedAtDesc()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "v1" };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Task.Delay(5, TestContext.Current.CancellationToken);
        entity.Name = "v2";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Task.Delay(5, TestContext.Current.CancellationToken);
        entity.Name = "v3";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        IAuditLog audit = new AuditLogService(db);
        var history = await audit.GetHistoryAsync<TestAuditedEntity>(entity.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, history.Count);
        Assert.True(history[0].ChangedAt >= history[1].ChangedAt);
        Assert.True(history[1].ChangedAt >= history[2].ChangedAt);
        Assert.Equal(AuditAction.Modify, history[0].Action);
        Assert.Equal(AuditAction.Add, history[2].Action);
    }

    [Fact]
    public async Task GetHistoryAsync_FiltersByEntityTypeAndId()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var keep = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "keep" };
        var noise = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "noise" };
        db.AuditedEntities.AddRange(keep, noise);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        IAuditLog audit = new AuditLogService(db);
        var history = await audit.GetHistoryAsync<TestAuditedEntity>(keep.Id, TestContext.Current.CancellationToken);

        Assert.Single(history);
        Assert.Equal(keep.Id.ToString(), history[0].EntityId);
    }

    [Fact]
    public async Task Suppress_SkipsAuditWritesViaService()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        IAuditLog audit = new AuditLogService(db);

        using (audit.Suppress())
        {
            db.AuditedEntities.Add(new TestAuditedEntity { Id = Guid.NewGuid(), Name = "x" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }
}
