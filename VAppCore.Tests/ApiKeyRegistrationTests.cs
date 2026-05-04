using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ApiKeyRegistrationTests
{
    [Fact]
    public void AddVApiKeyAuth_RegistersIApiKeyService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVApiKeyAuth<TestDbContext>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetService<IApiKeyService>();
        Assert.NotNull(svc);
        Assert.IsType<ApiKeyService>(svc);
    }

    [Fact]
    public async Task AddVApiKey_RegistersAuthScheme()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(opts => opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddVApiKeyAuth<TestDbContext>();
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName).AddVApiKey();
        services.AddLogging();
        services.AddOptions<VAppCoreOptions>();

        var sp = services.BuildServiceProvider();
        var schemeProvider = sp.GetRequiredService<IAuthenticationSchemeProvider>();
        var scheme = await schemeProvider.GetSchemeAsync(ApiKeyAuthenticationHandler.SchemeName);

        Assert.NotNull(scheme);
        Assert.Equal(typeof(ApiKeyAuthenticationHandler), scheme.HandlerType);
    }
}
