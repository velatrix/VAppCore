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

    [Fact]
    public async Task HardDelete_Audited_WritesAuditRow_WithActionDelete()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "Doomed", Score = 99 };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.AuditedEntities.Remove(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleteRow = await db.AuditLogs
            .Where(a => a.Action == AuditAction.Delete)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(entity.Id.ToString(), deleteRow.EntityId);

        using var doc = JsonDocument.Parse(deleteRow.Changes);
        Assert.True(doc.RootElement.TryGetProperty("name", out var name));
        Assert.Equal("Doomed", name.GetProperty("old").GetString());
        Assert.Equal(JsonValueKind.Null, name.GetProperty("new").ValueKind);
    }

    [Fact]
    public async Task SoftDelete_Audited_WritesAuditRow_WithActionDelete_NotModify()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedSoftDeletable { Id = Guid.NewGuid(), Name = "Soft" };
        db.AuditedSoftDeletables.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.AuditedSoftDeletables.Remove(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.AuditLogs
            .Where(a => a.EntityType == nameof(TestAuditedSoftDeletable))
            .OrderBy(a => a.ChangedAt)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rows.Count);
        Assert.Equal(AuditAction.Add, rows[0].Action);
        Assert.Equal(AuditAction.Delete, rows[1].Action);

        using var doc = JsonDocument.Parse(rows[1].Changes);
        Assert.False(doc.RootElement.TryGetProperty("isDeleted", out _));
        Assert.False(doc.RootElement.TryGetProperty("deletedAt", out _));
        Assert.False(doc.RootElement.TryGetProperty("deletedBy", out _));
    }

    [Fact]
    public async Task NonAuditedEntity_NoAuditRowsWritten()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var simple = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "ignored" };
        db.SimpleEntities.Add(simple);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        simple.Name = "still ignored";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.SimpleEntities.Remove(simple);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await db.AuditLogs.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Modify_Audited_DiffSkipsAuditAndConcurrencyFields()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedEntity { Id = Guid.NewGuid(), Name = "X", Score = 1 };
        db.AuditedEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        entity.Name = "Y";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var modify = await db.AuditLogs
            .Where(a => a.Action == AuditAction.Modify)
            .SingleAsync(TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(modify.Changes);
        Assert.False(doc.RootElement.TryGetProperty("createdAt", out _));
        Assert.False(doc.RootElement.TryGetProperty("updatedAt", out _));
        Assert.False(doc.RootElement.TryGetProperty("createdBy", out _));
        Assert.False(doc.RootElement.TryGetProperty("updatedBy", out _));
    }

    [Fact]
    public async Task Modify_NotAuditedField_ExcludedFromDiff()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedWithSkippedField { Id = Guid.NewGuid(), Name = "Old", LoginCount = 0 };
        db.AuditedWithSkipped.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        entity.Name = "New";
        entity.LoginCount = 99;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var modify = await db.AuditLogs
            .Where(a => a.Action == AuditAction.Modify
                     && a.EntityType == nameof(TestAuditedWithSkippedField))
            .SingleAsync(TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(modify.Changes);
        Assert.True(doc.RootElement.TryGetProperty("name", out _));
        Assert.False(doc.RootElement.TryGetProperty("loginCount", out _),
            "[NotAudited] property must be excluded from the diff");
    }

    [Fact]
    public async Task Modify_OnlyNotAuditedFieldsChanged_WritesNoAuditRow()
    {
        var (db, _) = TestFactory.CreateAuditLogDbContext();
        var entity = new TestAuditedWithSkippedField { Id = Guid.NewGuid(), Name = "Same", LoginCount = 1 };
        db.AuditedWithSkipped.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        entity.LoginCount = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var modifyCount = await db.AuditLogs
            .CountAsync(a => a.Action == AuditAction.Modify
                          && a.EntityType == nameof(TestAuditedWithSkippedField),
                        TestContext.Current.CancellationToken);
        Assert.Equal(0, modifyCount);
    }
}
