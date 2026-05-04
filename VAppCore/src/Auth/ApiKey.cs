using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace VAppCore;

/// <summary>
/// Service-to-service / machine API key. Plaintext is shown ONCE on create; only the
/// SHA-256 hex is persisted. Implements <see cref="IAuditedEntity"/> so create/revoke/rotate
/// auto-record into the v1.7 audit log when wired (the hashed secret is excluded via
/// <see cref="NotAuditedAttribute"/>).
/// </summary>
[Index(nameof(HashedSecret), IsUnique = true)]
[Index(nameof(Prefix))]
public class ApiKey : VEntity<Guid, Guid, Guid>, IAuditedEntity
{
    /// <summary>Human-readable key name (e.g. "core-game-server-prod").</summary>
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>First 12 chars of the plaintext (e.g. "vk_live_a1b2") — for admin UI display only.</summary>
    [MaxLength(20)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (lowercase, 64 chars) of the full plaintext key. Used for indexed lookup.</summary>
    [MaxLength(64)]
    [NotAudited]
    public string HashedSecret { get; set; } = string.Empty;

    /// <summary>JSON-serialized array of permission strings. Use <see cref="Permissions"/> for list access.</summary>
    [Column(TypeName = "jsonb")]
    public string PermissionsJson { get; set; } = "[]";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Convenience accessor; deserializes <see cref="PermissionsJson"/> on each call.</summary>
    [NotMapped]
    public IReadOnlyList<string> Permissions
    {
        get => JsonSerializer.Deserialize<List<string>>(PermissionsJson) ?? new();
        set => PermissionsJson = JsonSerializer.Serialize(value.ToList());
    }
}
