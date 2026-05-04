using Microsoft.EntityFrameworkCore;

namespace VAppCore;

/// <summary>
/// Default <see cref="IAuditLog"/> implementation backed by a <see cref="DbContext"/>'s
/// <see cref="DbSet{TEntity}"/> of <see cref="AuditLog"/>.
/// </summary>
public sealed class AuditLogService : IAuditLog
{
    private readonly DbContext _db;

    public AuditLogService(DbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<AuditLog>> GetHistoryAsync<TEntity>(object id, CancellationToken ct = default)
        => GetHistoryAsync(typeof(TEntity).Name, id?.ToString() ?? string.Empty, ct);

    public async Task<IReadOnlyList<AuditLog>> GetHistoryAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        var rows = await _db.Set<AuditLog>()
            .Where(a => a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.ChangedAt)
            .ToListAsync(ct);
        return rows;
    }

    public IDisposable Suppress() => AuditSuppression.Suppress();
}
