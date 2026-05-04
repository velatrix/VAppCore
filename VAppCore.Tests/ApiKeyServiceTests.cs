using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ApiKeyServiceTests
{
    private static (TestDbContext Db, ApiKeyService Svc) CreateService()
    {
        var (db, _) = TestFactory.CreateDbContext();
        var svc = new ApiKeyService(db);
        return (db, svc);
    }

    [Fact]
    public async Task CreateAsync_PersistsKey_AndReturnsPlaintextOnce()
    {
        var (db, svc) = CreateService();
        var (key, plaintext) = await svc.CreateAsync(
            "core-server-prod",
            new[] { "matches.report" },
            expiresAt: null,
            ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("vk_live_", plaintext);
        Assert.Equal("core-server-prod", key.Name);
        Assert.Equal(ApiKeyHasher.ExtractPrefix(plaintext), key.Prefix);
        Assert.Equal(ApiKeyHasher.Hash(plaintext), key.HashedSecret);
        Assert.True(key.IsActive);
        Assert.Null(key.ExpiresAt);
        Assert.Contains("matches.report", key.Permissions);

        var stored = await db.ApiKeys.FindAsync(new object[] { key.Id }, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(key.Id, stored.Id);
    }

    [Fact]
    public async Task CreateAsync_DoesNotPersistPlaintext()
    {
        var (db, svc) = CreateService();
        var (_, plaintext) = await svc.CreateAsync(
            "k", new[] { "p" }, ct: TestContext.Current.CancellationToken);

        var allRows = await db.ApiKeys.ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(allRows, r => r.HashedSecret == plaintext);
    }

    [Fact]
    public async Task CreateAsync_StoresMultiplePermissions()
    {
        var (_, svc) = CreateService();
        var (key, _) = await svc.CreateAsync(
            "k",
            new[] { "matches.report", "matches.read", "lobbies.read" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, key.Permissions.Count);
        Assert.Contains("matches.report", key.Permissions);
        Assert.Contains("matches.read", key.Permissions);
        Assert.Contains("lobbies.read", key.Permissions);
    }

    [Fact]
    public async Task CreateAsync_WithExpiry_StoresExpiry()
    {
        var (_, svc) = CreateService();
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var (key, _) = await svc.CreateAsync(
            "k", new[] { "p" }, expiresAt: expiry, ct: TestContext.Current.CancellationToken);

        Assert.Equal(expiry, key.ExpiresAt);
    }
}
