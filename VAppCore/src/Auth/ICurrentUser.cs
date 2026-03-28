namespace VAppCore;

/// <summary>
/// Non-generic interface for authorization checks.
/// Used by VAuthorizeFilter and anywhere that doesn't need typed keys.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    bool IsInRole(string role);
    bool HasPermission(string permission);
}

/// <summary>
/// Generic interface with typed user and tenant keys.
/// Used by VDbContext and VService for audit fields and tenant scoping.
/// </summary>
public interface ICurrentUser<TUserKey, TTenantKey> : ICurrentUser
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    TUserKey UserId { get; }
    TTenantKey TenantId { get; }
    string? Email { get; }
    IReadOnlyList<string> Roles { get; }
    IReadOnlyList<string> Permissions { get; }
}
