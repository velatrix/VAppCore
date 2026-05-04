namespace VAppCore;

/// <summary>
/// Non-generic interface for authorization checks.
/// Used by VAuthorizeFilter and anywhere that doesn't need typed keys.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>
    /// The authentication scheme that produced this principal (e.g. <c>"Cookies"</c>, <c>"ApiKey"</c>).
    /// Null for unauthenticated callers. Used by <see cref="VAuthorizeAttribute.ApiKey"/> to require
    /// the caller is the API key scheme.
    /// </summary>
    string? AuthenticationType { get; }

    bool IsInRole(string role);
    bool HasPermission(string permission);
}

/// <summary>
/// Generic interface with typed user and tenant keys.
/// Used by VAuditInterceptor and VService for audit fields and tenant scoping.
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
