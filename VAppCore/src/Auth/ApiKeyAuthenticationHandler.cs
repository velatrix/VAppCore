using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// Authentication handler that reads <see cref="ApiKeyAuthenticationOptions.HeaderName"/>,
/// hashes via <see cref="ApiKeyHasher"/>, validates against <see cref="IApiKeyService"/>,
/// and produces a <see cref="ClaimsPrincipal"/> with <c>AuthenticationType = "ApiKey"</c>.
/// User-id and permission claims use <see cref="VAppCoreOptions.UserIdClaim"/> and
/// <see cref="VAppCoreOptions.PermissionClaim"/> so <see cref="ICurrentUser"/> reads them uniformly.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public const string SchemeName = "ApiKey";

    private readonly IApiKeyService _service;
    private readonly VAppCoreOptions _coreOptions;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyService service,
        IOptions<VAppCoreOptions> coreOptions)
        : base(options, logger, encoder)
    {
        _service = service;
        _coreOptions = coreOptions.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Options.HeaderName, out var values))
            return AuthenticateResult.NoResult();

        var plaintext = values.ToString();
        if (string.IsNullOrEmpty(plaintext))
            return AuthenticateResult.NoResult();

        var key = await _service.AuthenticateAsync(plaintext, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Invalid, revoked, or expired API key");

        var claims = new List<Claim>
        {
            new(_coreOptions.UserIdClaim, key.Id.ToString()),
            new(ClaimTypes.Name, key.Name)
        };
        foreach (var perm in key.Permissions)
            claims.Add(new Claim(_coreOptions.PermissionClaim, perm));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        // Fire-and-forget LastUsedAt update — not awaited; failure here must not block the request,
        // but we do want failures to be observable rather than silently swallowed.
        _ = _service.MarkUsedAsync(key.Id, CancellationToken.None)
            .ContinueWith(
                t => Logger.LogWarning(t.Exception, "MarkUsedAsync failed for ApiKey {ApiKeyId}", key.Id),
                TaskContinuationOptions.OnlyOnFaulted);

        return AuthenticateResult.Success(ticket);
    }
}
