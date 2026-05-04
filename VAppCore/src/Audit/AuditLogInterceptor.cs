using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VAppCore;

/// <summary>
/// SaveChanges interceptor that writes one <see cref="AuditLog"/> row per change to an
/// <see cref="IAuditedEntity"/>. Skipped entirely for entities that don't implement the marker.
/// The audit row inserts via the same DbContext, so it commits with the entity change atomically.
/// Honors <see cref="AuditSuppression"/> for bulk operations.
/// The consuming DbContext must expose <c>DbSet&lt;AuditLog&gt;</c>; the interceptor throws
/// <see cref="InvalidOperationException"/> on the first save if it isn't registered.
/// </summary>
public sealed class AuditLogInterceptor<TUserKey, TTenantKey> : ISaveChangesInterceptor
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Always-skipped property names (audit fields, concurrency tokens, soft-delete signal)
    private static readonly HashSet<string> AlwaysSkip =
    [
        nameof(IVAudited<int>.CreatedAt),
        nameof(IVAudited<int>.UpdatedAt),
        nameof(IVAudited<int>.CreatedBy),
        nameof(IVAudited<int>.UpdatedBy),
        nameof(IConcurrent.RowVersion),
        nameof(IConcurrentXmin.Xmin),
        nameof(ISoftDeletable.IsDeleted),
        nameof(ISoftDeletable.DeletedAt),
        VAuditInterceptor<int, int>.DeletedByPropertyName
    ];

    // Cache of [NotAudited] property names per entity type
    private static readonly ConcurrentDictionary<Type, HashSet<string>> NotAuditedCache = new();

    private readonly ICurrentUser<TUserKey, TTenantKey>? _currentUser;

    public AuditLogInterceptor(ICurrentUser<TUserKey, TTenantKey>? currentUser = null)
    {
        _currentUser = currentUser;
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Process(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Process(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Process(DbContext? db)
    {
        if (db is null) return;
        if (AuditSuppression.IsSuppressed) return;

        if (db.Model.FindEntityType(typeof(AuditLog)) is null)
            throw new InvalidOperationException(
                $"{nameof(AuditLogInterceptor<TUserKey, TTenantKey>)} requires DbSet<AuditLog> to be registered on the DbContext.");

        var changedAt = DateTimeOffset.UtcNow;
        var changedBy = _currentUser is { IsAuthenticated: true }
            ? _currentUser.UserId.ToString()
            : null;

        // Snapshot first — we'll be adding AuditLog rows which mutates the change tracker.
        var entries = db.ChangeTracker.Entries()
            .Where(e => e.Entity is IAuditedEntity)
            .ToList();

        foreach (var entry in entries)
        {
            var row = TryBuildAuditLog(entry, changedAt, changedBy);
            if (row is not null)
                db.Add(row);
        }
    }

    private static AuditLog? TryBuildAuditLog(EntityEntry entry, DateTimeOffset changedAt, string? changedBy)
    {
        var action = entry.State switch
        {
            EntityState.Added => AuditAction.Add,
            EntityState.Modified => AuditAction.Modify,
            EntityState.Deleted => AuditAction.Delete,
            _ => (AuditAction?)null
        };
        if (action is null) return null;

        var diff = BuildDiff(entry, action.Value);
        if (action == AuditAction.Modify && diff.Count == 0) return null;

        return new AuditLog
        {
            EntityType = entry.Entity.GetType().Name,
            EntityId = ExtractEntityId(entry),
            Action = action.Value,
            ChangedAt = changedAt,
            ChangedBy = changedBy,
            Changes = JsonSerializer.Serialize(diff, JsonOpts)
        };
    }

    private static Dictionary<string, AuditChange> BuildDiff(EntityEntry entry, AuditAction action)
    {
        var skip = NotAuditedCache.GetOrAdd(entry.Entity.GetType(), BuildNotAuditedSet);
        var diff = new Dictionary<string, AuditChange>();

        foreach (var prop in entry.Properties)
        {
            var name = prop.Metadata.Name;
            if (AlwaysSkip.Contains(name)) continue;
            if (skip.Contains(name)) continue;
            if (action == AuditAction.Modify && !prop.IsModified) continue;
            if (action == AuditAction.Modify &&
                Equals(prop.OriginalValue, prop.CurrentValue)) continue;

            var oldVal = action == AuditAction.Add ? null : prop.OriginalValue;
            var newVal = action == AuditAction.Delete ? null : prop.CurrentValue;
            diff[JsonNamingPolicy.CamelCase.ConvertName(name)] = new AuditChange(oldVal, newVal);
        }

        return diff;
    }

    private static string ExtractEntityId(EntityEntry entry)
    {
        var pk = entry.Metadata.FindPrimaryKey();
        if (pk is null) return string.Empty;
        var values = pk.Properties
            .Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? string.Empty);
        return string.Join(",", values);
    }

    private static HashSet<string> BuildNotAuditedSet(Type t)
    {
        return [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<NotAuditedAttribute>() is not null)
            .Select(p => p.Name)];
    }

}
