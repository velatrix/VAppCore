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

    public async Task RevokeAsync(Guid keyId, CancellationToken ct = default)
    {
        var key = await _db.Set<ApiKey>().FindAsync(new object[] { keyId }, ct);
        if (key is null) return;
        if (!key.IsActive) return;
        key.IsActive = false;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(ApiKey Key, string Plaintext)> RotateAsync(Guid keyId, CancellationToken ct = default)
    {
        var existing = await _db.Set<ApiKey>().FindAsync(new object[] { keyId }, ct)
            ?? throw new InvalidOperationException($"ApiKey {keyId} not found.");

        existing.IsActive = false;

        var plaintext = ApiKeyHasher.GeneratePlaintext();
        var newKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = existing.Name,
            Prefix = ApiKeyHasher.ExtractPrefix(plaintext),
            HashedSecret = ApiKeyHasher.Hash(plaintext),
            Permissions = existing.Permissions.ToList(),
            IsActive = true,
            ExpiresAt = existing.ExpiresAt
        };
        _db.Set<ApiKey>().Add(newKey);
        await _db.SaveChangesAsync(ct);
        return (newKey, plaintext);
    }

    public async Task<ApiKey?> GetAsync(Guid keyId, CancellationToken ct = default)
        => await _db.Set<ApiKey>().FindAsync(new object[] { keyId }, ct);

    public async Task<IReadOnlyList<ApiKey>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        var q = _db.Set<ApiKey>().AsQueryable();
        if (!includeInactive)
            q = q.Where(k => k.IsActive);
        return await q.ToListAsync(ct);
    }

    public async Task MarkUsedAsync(Guid keyId, CancellationToken ct = default)
    {
        var key = await _db.Set<ApiKey>().FindAsync(new object[] { keyId }, ct);
        if (key is null) return;
        key.LastUsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
