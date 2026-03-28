namespace VAppCore;

public abstract class VEntity<TKey, TUserKey, TTenantKey>
    where TKey : IEquatable<TKey>
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    public TKey Id { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public TUserKey CreatedBy { get; set; } = default!;
    public TUserKey UpdatedBy { get; set; } = default!;
}

/// <summary>
/// Convenience base entity with Guid keys for all types.
/// </summary>
public abstract class VEntity : VEntity<Guid, Guid, Guid> { }
