namespace VAppCore;

/// <summary>
/// Marker interface for domain events. Implement on a record/class describing "something happened"
/// in the domain (e.g. <c>UserRegistered</c>, <c>MatchCompleted</c>). Raise via
/// <see cref="VEntity{TKey, TUserKey, TTenantKey}.RaiseEvent"/> on an entity being saved;
/// the OutboxInterceptor persists the event to the outbox in the same transaction as the entity changes,
/// then the OutboxProcessor delivers it to all registered <see cref="IDomainEventHandler{TEvent}"/> handlers.
/// </summary>
public interface IDomainEvent { }

/// <summary>
/// Implement on a class that reacts to a domain event. Multiple handlers may handle the same event;
/// they run sequentially per outbox row dispatch. Handlers must be idempotent — the outbox guarantees
/// at-least-once delivery, not exactly-once.
/// </summary>
public interface IDomainEventHandler<TEvent> where TEvent : IDomainEvent
{
    Task Handle(TEvent evt, EventContext context, CancellationToken ct);
}

/// <summary>
/// Per-dispatch context handed to every <see cref="IDomainEventHandler{TEvent}.Handle"/> invocation.
/// <c>MessageId</c> is the unique outbox row id — handlers can use it as an idempotency key when they
/// need to dedupe (e.g. by writing to a ProcessedEvents table). <c>Attempt</c> is the 1-based retry
/// count — useful for "log only on first attempt" or "alert on attempt 5+" logic.
/// </summary>
public record EventContext(Guid MessageId, int Attempt, DateTimeOffset OccurredAt);
