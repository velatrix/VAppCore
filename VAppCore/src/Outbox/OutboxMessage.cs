namespace VAppCore;

public enum OutboxStatus
{
    Pending = 0,
    Sent = 1,
    DeadLettered = 2
}

/// <summary>
/// Persistent record of a domain event awaiting (or that has completed) dispatch.
/// Inserted by <c>OutboxInterceptor</c> in the same transaction as the entity changes that
/// raised the event. Picked up by <c>OutboxProcessor</c> on its next polling pass.
/// </summary>
public class OutboxMessage : VEntity<Guid, Guid, Guid>
{
    /// <summary>Assembly-qualified type name of the domain event, used to deserialize Payload.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON-serialized event payload.</summary>
    public string Payload { get; set; } = string.Empty;

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>Number of dispatch attempts that have been made (success or failure).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Earliest time the processor may attempt the next dispatch. Set when an attempt fails
    /// (computed from exponential backoff). Null = eligible for immediate dispatch.
    /// </summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>Most recent failure message, for observability.</summary>
    public string? LastError { get; set; }

    /// <summary>Set when <see cref="Status"/> becomes <see cref="OutboxStatus.Sent"/>.</summary>
    public DateTimeOffset? SentAt { get; set; }
}
