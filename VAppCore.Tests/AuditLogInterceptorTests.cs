using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class AuditLogInterceptorTests
{
    [Fact]
    public async Task Add_Audited_WritesAuditRow_WithActionAdd()
    {
        var (db, user) = TestFactory.CreateAuditLogDbContext();

        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "First", Score = 10 };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(rows);
        Assert.Equal(nameof(TestAuditedEntity), row.EntityType);
        Assert.Equal(entity.Id.ToString(), row.EntityId);
        Assert.Equal(AuditAction.Add, row.Action);
        Assert.Equal(user.UserId.ToString(), row.ChangedBy);
        Assert.NotEqual(default, row.ChangedAt);

        using var doc = JsonDocument.Parse(row.Changes);
        Assert.True(doc.RootElement.TryGetProperty("name", out var name));
        Assert.Null(name.GetProperty("old").GetString());
        Assert.Equal("First", name.GetProperty("new").GetString());

        Assert.True(doc.RootElement.TryGetProperty("score", out var score));
        Assert.Equal(10, score.GetProperty("new").GetInt32());
    }

    [Fact]
    public async Task Modify_Audited_WritesAuditRow_WithActionModify_AndOnlyChangedFields()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "Initial", Score = 10 };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        entity.Name = "Updated";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.AuditLogs.OrderBy(a => a.ChangedAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
        var modify = rows[1];
        Assert.Equal(AuditAction.Modify, modify.Action);

        using var doc = JsonDocument.Parse(modify.Changes);
        Assert.True(doc.RootElement.TryGetProperty("name", out var name));
        Assert.Equal("Initial", name.GetProperty("old").GetString());
        Assert.Equal("Updated", name.GetProperty("new").GetString());
        Assert.False(doc.RootElement.TryGetProperty("score", out _),
            "Unchanged fields must not appear in Modify diff");
    }

    [Fact]
    public async Task Modify_NoFieldChanges_WritesNoAuditRow()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "X" };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.AuditedEntities.Update(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(rows);
    }
}
