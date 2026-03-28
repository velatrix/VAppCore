namespace VAppCore;

/// <summary>
/// Opt-in interface for soft delete. Entities implementing this will have
/// Remove() intercepted by VDbContext — setting IsDeleted instead of deleting.
/// Add a DeletedBy property (matching TUserKey type) to track who deleted it.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
}
