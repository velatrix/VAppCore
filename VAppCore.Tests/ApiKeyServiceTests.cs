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

    [Fact]
    public async Task RevokeAsync_FlipsIsActive_AndAuthenticateReturnsNull()
    {
        var (_, svc) = CreateService();
        var (created, plaintext) = await svc.CreateAsync(
            "k", new[] { "p" }, ct: TestContext.Current.CancellationToken);

        await svc.RevokeAsync(created.Id, TestContext.Current.CancellationToken);

        var fetched = await svc.GetAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.False(fetched.IsActive);

        var auth = await svc.AuthenticateAsync(plaintext, TestContext.Current.CancellationToken);
        Assert.Null(auth);
    }

    [Fact]
    public async Task RevokeAsync_UnknownId_DoesNothing()
    {
        var (_, svc) = CreateService();
        await svc.RevokeAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        // No throw expected.
    }

    [Fact]
    public async Task GetAsync_ReturnsKey()
    {
        var (_, svc) = CreateService();
        var (created, _) = await svc.CreateAsync("k", new[] { "p" }, ct: TestContext.Current.CancellationToken);

        var fetched = await svc.GetAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        var (_, svc) = CreateService();
        Assert.Null(await svc.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAsync_DefaultsExcludeInactive()
    {
        var (_, svc) = CreateService();
        var (active, _) = await svc.CreateAsync("active", new[] { "p" }, ct: TestContext.Current.CancellationToken);
        var (inactive, _) = await svc.CreateAsync("inactive", new[] { "p" }, ct: TestContext.Current.CancellationToken);
        await svc.RevokeAsync(inactive.Id, TestContext.Current.CancellationToken);

        var list = await svc.ListAsync(includeInactive: false, ct: TestContext.Current.CancellationToken);
        Assert.Single(list);
        Assert.Equal(active.Id, list[0].Id);
    }

    [Fact]
    public async Task ListAsync_IncludeInactiveTrue_ReturnsAll()
    {
        var (_, svc) = CreateService();
        var (active, _) = await svc.CreateAsync("active", new[] { "p" }, ct: TestContext.Current.CancellationToken);
        var (inactive, _) = await svc.CreateAsync("inactive", new[] { "p" }, ct: TestContext.Current.CancellationToken);
        await svc.RevokeAsync(inactive.Id, TestContext.Current.CancellationToken);

        var list = await svc.ListAsync(includeInactive: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task RotateAsync_RevokesOld_CreatesNewWithSameNameAndPermissions()
    {
        var (_, svc) = CreateService();
        var (oldKey, oldPlain) = await svc.CreateAsync(
            "rotate-me",
            new[] { "matches.report", "matches.read" },
            ct: TestContext.Current.CancellationToken);

        var (newKey, newPlain) = await svc.RotateAsync(oldKey.Id, TestContext.Current.CancellationToken);

        Assert.NotEqual(oldKey.Id, newKey.Id);
        Assert.NotEqual(oldPlain, newPlain);
        Assert.Equal(oldKey.Name, newKey.Name);
        Assert.Equal(oldKey.Permissions, newKey.Permissions);
        Assert.True(newKey.IsActive);

        var oldFetched = await svc.GetAsync(oldKey.Id, TestContext.Current.CancellationToken);
        Assert.False(oldFetched!.IsActive);
    }

    [Fact]
    public async Task RotateAsync_UnknownId_ThrowsInvalidOperation()
    {
        var (_, svc) = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RotateAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MarkUsedAsync_UpdatesLastUsedAt()
    {
        var (_, svc) = CreateService();
        var (created, _) = await svc.CreateAsync("k", new[] { "p" }, ct: TestContext.Current.CancellationToken);
        Assert.Null(created.LastUsedAt);

        await svc.MarkUsedAsync(created.Id, TestContext.Current.CancellationToken);

        var fetched = await svc.GetAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched!.LastUsedAt);
    }
}
