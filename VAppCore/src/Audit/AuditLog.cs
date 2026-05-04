using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace VAppCore;

/// <summary>
/// One row per change to an <see cref="IAuditedEntity"/>. Inserted by
/// <see cref="AuditLogInterceptor{TUserKey, TTenantKey}"/> in the same SaveChanges call
/// as the entity change, so the audit row commits with the change atomically.
/// </summary>
[Index(nameof(EntityType), nameof(EntityId))]
[Index(nameof(ChangedAt))]
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>CLR type name of the audited entity (e.g. "Lobby"). Not assembly-qualified.</summary>
    [MaxLength(200)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Primary key of the audited entity, serialized via <c>ToString()</c>.</summary>
    [MaxLength(500)]
    public string EntityId { get; set; } = string.Empty;

    public AuditAction Action { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>User key serialized via <c>ToString()</c>. Null when the change was made unauthenticated.</summary>
    public string? ChangedBy { get; set; }

    /// <summary>JSON object: <c>{ propName: { old, new }, ... }</c>. Stored as <c>jsonb</c> on Postgres.</summary>
    [Column(TypeName = "jsonb")]
    public string Changes { get; set; } = "{}";
}
