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

    [Fact]
    public async Task AuthenticateAsync_ValidKey_ReturnsApiKey()
    {
        var (_, svc) = CreateService();
        var (created, plaintext) = await svc.CreateAsync(
            "k", new[] { "p" }, ct: TestContext.Current.CancellationToken);

        var auth = await svc.AuthenticateAsync(plaintext, TestContext.Current.CancellationToken);
        Assert.NotNull(auth);
        Assert.Equal(created.Id, auth.Id);
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownKey_ReturnsNull()
    {
        var (_, svc) = CreateService();
        var auth = await svc.AuthenticateAsync("vk_live_unknown_key_value_string_padding_zzzz", TestContext.Current.CancellationToken);
        Assert.Null(auth);
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedKey_ReturnsNull()
    {
        var (db, svc) = CreateService();
        var (created, plaintext) = await svc.CreateAsync(
            "k", new[] { "p" }, ct: TestContext.Current.CancellationToken);

        // Manually flip IsActive (RevokeAsync isn't implemented until Task 5)
        created.IsActive = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var auth = await svc.AuthenticateAsync(plaintext, TestContext.Current.CancellationToken);
        Assert.Null(auth);
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiredKey_ReturnsNull()
    {
        var (_, svc) = CreateService();
        var (_, plaintext) = await svc.CreateAsync(
            "k",
            new[] { "p" },
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            ct: TestContext.Current.CancellationToken);

        var auth = await svc.AuthenticateAsync(plaintext, TestContext.Current.CancellationToken);
        Assert.Null(auth);
    }

    [Fact]
    public async Task AuthenticateAsync_EmptyOrNullKey_ReturnsNull()
    {
        var (_, svc) = CreateService();
        Assert.Null(await svc.AuthenticateAsync(string.Empty, TestContext.Current.CancellationToken));
    }
}
