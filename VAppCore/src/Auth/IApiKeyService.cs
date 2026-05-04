namespace VAppCore;

/// <summary>
/// Manages API key lifecycle: create (plaintext returned once), authenticate, revoke, rotate, list.
/// Plaintext is shown to the caller of <see cref="CreateAsync"/> and <see cref="RotateAsync"/>
/// once and never persisted — only the SHA-256 hex is stored.
/// </summary>
public interface IApiKeyService
{
    /// <summary>Creates a new API key. The returned plaintext is the only time it is available — store it now.</summary>
    Task<(ApiKey Key, string Plaintext)> CreateAsync(
        string name,
        IEnumerable<string> permissions,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    /// <summary>Validates a plaintext key. Returns the matching <see cref="ApiKey"/> if active and unexpired; null otherwise.</summary>
    Task<ApiKey?> AuthenticateAsync(string plaintextKey, CancellationToken ct = default);

    /// <summary>Marks the key inactive. Subsequent authentications will return null.</summary>
    Task RevokeAsync(Guid keyId, CancellationToken ct = default);

    /// <summary>Revokes the existing key and creates a new one with the same name and permissions. Returns the new plaintext once.</summary>
    Task<(ApiKey Key, string Plaintext)> RotateAsync(Guid keyId, CancellationToken ct = default);

    /// <summary>Get a single key by id (no plaintext available).</summary>
    Task<ApiKey?> GetAsync(Guid keyId, CancellationToken ct = default);

    /// <summary>List keys. Excludes <c>IsActive == false</c> by default.</summary>
    Task<IReadOnlyList<ApiKey>> ListAsync(bool includeInactive = false, CancellationToken ct = default);

    /// <summary>Updates <see cref="ApiKey.LastUsedAt"/>. Called by the auth handler on successful authentication.</summary>
    Task MarkUsedAsync(Guid keyId, CancellationToken ct = default);
}
