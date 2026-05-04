using Microsoft.EntityFrameworkCore;

namespace VAppCore;

public sealed class ApiKeyService : IApiKeyService
{
    private readonly DbContext _db;

    public ApiKeyService(DbContext db)
    {
        _db = db;
    }

    public async Task<(ApiKey Key, string Plaintext)> CreateAsync(
        string name,
        IEnumerable<string> permissions,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        var plaintext = ApiKeyHasher.GeneratePlaintext();
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = name,
            Prefix = ApiKeyHasher.ExtractPrefix(plaintext),
            HashedSecret = ApiKeyHasher.Hash(plaintext),
            Permissions = permissions.ToList(),
            IsActive = true,
            ExpiresAt = expiresAt
        };
        _db.Set<ApiKey>().Add(key);
        await _db.SaveChangesAsync(ct);
        return (key, plaintext);
    }

    public async Task<ApiKey?> AuthenticateAsync(string plaintextKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintextKey)) return null;

        var hash = ApiKeyHasher.Hash(plaintextKey);
        var key = await _db.Set<ApiKey>()
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.HashedSecret == hash, ct);

        if (key is null) return null;
        if (!key.IsActive) return null;
        if (key.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) return null;

        return key;
    }

    public Task RevokeAsync(Guid keyId, CancellationToken ct = default)
        => throw new NotImplementedException("Wired in Task 5");

    public Task<(ApiKey Key, string Plaintext)> RotateAsync(Guid keyId, CancellationToken ct = default)
        => throw new NotImplementedException("Wired in Task 5");

    public Task<ApiKey?> GetAsync(Guid keyId, CancellationToken ct = default)
        => throw new NotImplementedException("Wired in Task 5");

    public Task<IReadOnlyList<ApiKey>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
        => throw new NotImplementedException("Wired in Task 5");

    public Task MarkUsedAsync(Guid keyId, CancellationToken ct = default)
        => throw new NotImplementedException("Wired in Task 5");
}
