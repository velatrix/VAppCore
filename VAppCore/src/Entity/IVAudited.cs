namespace VAppCore;

/// <summary>
/// Marker interface for entities that carry audit fields. VAuditInterceptor
/// populates these on Add (CreatedAt/UpdatedAt/CreatedBy/UpdatedBy) and Modify
/// (UpdatedAt/UpdatedBy, with CreatedAt/CreatedBy preserved).
/// VEntity implements this; consumers normally don't implement it directly.
/// </summary>
public interface IVAudited<TUserKey>
    where TUserKey : IEquatable<TUserKey>
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
    TUserKey CreatedBy { get; set; }
    TUserKey UpdatedBy { get; set; }
}
