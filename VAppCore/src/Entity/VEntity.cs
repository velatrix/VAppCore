using System.ComponentModel.DataAnnotations.Schema;

namespace VAppCore;

public abstract class VEntity<TKey, TUserKey, TTenantKey> : IVAudited<TUserKey>
    where TKey : IEquatable<TKey>
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    public TKey Id { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public TUserKey CreatedBy { get; set; } = default!;
    public TUserKey UpdatedBy { get; set; } = default!;

    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Domain events raised on this entity since last save. The OutboxInterceptor
    /// extracts these during SaveChanges and writes them as OutboxMessage rows in the
    /// same transaction. Not mapped to the database.
    /// </summary>
    [NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>Append a domain event to be persisted to the outbox on the next SaveChanges.</summary>
    public void RaiseEvent(IDomainEvent evt) => _domainEvents.Add(evt);

    /// <summary>
    /// Clears the in-memory event list. Called by the OutboxInterceptor after extracting
    /// events into outbox rows; consumers normally don't call this directly.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>
/// Convenience base entity with Guid keys for all types.
/// </summary>
public abstract class VEntity : VEntity<Guid, Guid, Guid> { }
