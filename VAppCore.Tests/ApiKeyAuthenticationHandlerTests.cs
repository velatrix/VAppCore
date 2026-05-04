using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    private const string SchemeName = "ApiKey";

    private static async Task<(ApiKeyAuthenticationHandler Handler, HttpContext Ctx)> SetupAsync(
        string? headerValue, IApiKeyService svc, VAppCoreOptions? coreOptions = null)
    {
        var schemeOptions = new ApiKeyAuthenticationOptions();
        var optionsMonitor = new TestOptionsMonitor<ApiKeyAuthenticationOptions>(schemeOptions);
        var coreOpts = Options.Create(coreOptions ?? new VAppCoreOptions());
        var handler = new ApiKeyAuthenticationHandler(optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, svc, coreOpts);

        var scheme = new AuthenticationScheme(SchemeName, displayName: null, handlerType: typeof(ApiKeyAuthenticationHandler));
        var ctx = new DefaultHttpContext();
        if (headerValue is not null) ctx.Request.Headers["X-Api-Key"] = headerValue;
        await handler.InitializeAsync(scheme, ctx);
        return (handler, ctx);
    }

    [Fact]
    public async Task NoHeader_ReturnsNoResult()
    {
        var (db, _) = TestFactory.CreateDbContext();
        var svc = new ApiKeyService(db);
        var (handler, _) = await SetupAsync(headerValue: null, svc);

        var result = await handler.AuthenticateAsync();
        Assert.True(result.None);
    }

    [Fact]
    public async Task EmptyHeader_ReturnsNoResult()
    {
        var (db, _) = TestFactory.CreateDbContext();
        var svc = new ApiKeyService(db);
        var (handler, _) = await SetupAsync(headerValue: string.Empty, svc);

        var result = await handler.AuthenticateAsync();
        Assert.True(result.None);
    }

    [Fact]
    public async Task UnknownKey_Fails()
    {
        var (db, _) = TestFactory.CreateDbContext();
        var svc = new ApiKeyService(db);
        var (handler, _) = await SetupAsync(headerValue: "vk_live_does_not_exist_value_padding_zzzz", svc);

        var result = await handler.AuthenticateAsync();
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task ValidKey_SucceedsWithApiKeyAuthenticationType()
    {
        var (db, _) = TestFactory.CreateDbContext();
        var svc = new ApiKeyService(db);
        var (_, plaintext) = await svc.CreateAsync(
            "k", new[] { "matches.report" }, ct: TestContext.Current.CancellationToken);

        var (handler, _) = await SetupAsync(plaintext, svc);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        Assert.Equal("ApiKey", result.Principal.Identity!.AuthenticationType);
        Assert.True(result.Principal.Identity.IsAuthenticated);
    }

    [Fact]
    public async Task ValidKey_PrincipalCarriesUserIdAndPermissionsViaConfiguredClaims()
    {
        var (db, _) = TestFactory.CreateDbContext();
        var svc = new ApiKeyService(db);
        var (created, plaintext) = await svc.CreateAsync(
            "k",
            new[] { "matches.report", "matches.read" },
            ct: TestContext.Current.CancellationToken);

        var coreOptions = new VAppCoreOptions(); // defaults: UserIdClaim="sub", PermissionClaim="permission"
        var (handler, _) = await SetupAsync(plaintext, svc, coreOptions);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(created.Id.ToString(), result.Principal!.FindFirst("sub")!.Value);
        var permClaims = result.Principal.FindAll("permission").Select(c => c.Value).ToList();
        Assert.Contains("matches.report", permClaims);
        Assert.Contains("matches.read", permClaims);
    }
}

internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) { CurrentValue = value; }
    public T CurrentValue { get; }
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
