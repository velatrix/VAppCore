namespace VAppCore;

/// <summary>
/// Read access and suppression facade for the audit log. Inject into any service
/// that needs to display history or suppress auditing during bulk operations.
/// </summary>
public interface IAuditLog
{
    /// <summary>History rows for an entity, newest first.</summary>
    Task<IReadOnlyList<AuditLog>> GetHistoryAsync<TEntity>(object id, CancellationToken ct = default);

    /// <summary>History rows by raw entity-type name and id (for non-generic call sites).</summary>
    Task<IReadOnlyList<AuditLog>> GetHistoryAsync(string entityType, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Disposable scope that disables audit writes for the duration. Equivalent to
    /// <see cref="AuditSuppression.Suppress"/> — provided here so consumers don't need to
    /// reach for the static class.
    /// </summary>
    IDisposable Suppress();
}
