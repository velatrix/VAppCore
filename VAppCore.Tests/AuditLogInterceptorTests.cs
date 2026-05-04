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
}
