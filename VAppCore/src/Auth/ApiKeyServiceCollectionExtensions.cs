using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VAppCore;

public static class ApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IApiKeyService"/> backed by <typeparamref name="TDbContext"/>.
    /// Pair with <see cref="AddVApiKey"/> on the authentication builder to wire the auth scheme.
    /// The consuming DbContext must expose <c>DbSet&lt;ApiKey&gt;</c>.
    /// </summary>
    public static IServiceCollection AddVApiKeyAuth<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.TryAddScoped<IApiKeyService>(sp => new ApiKeyService(sp.GetRequiredService<TDbContext>()));
        return services;
    }

    /// <summary>
    /// Registers the <c>ApiKey</c> authentication scheme on the auth builder.
    /// Default scheme name is <see cref="ApiKeyAuthenticationHandler.SchemeName"/>.
    /// </summary>
    public static AuthenticationBuilder AddVApiKey(
        this AuthenticationBuilder builder,
        string scheme = ApiKeyAuthenticationHandler.SchemeName,
        Action<ApiKeyAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            scheme,
            displayName: null,
            configureOptions: configure ?? (_ => { }));
    }
}
