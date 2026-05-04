using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VAppCore.Tests;

public class RateLimitingMiddlewareTests
{
    private static async Task<TestServer> BuildServerAsync(
        Action<VAppCoreRateLimitingOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddLogging();
                    s.AddVAppCoreRateLimiting(configureOptions ?? (_ => { }));
                    configureServices?.Invoke(s);
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<VExceptionMiddleware>();
                    app.UseRouting();
                    app.UseVRateLimiting();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/auth/login", () => "ok")
                            .WithMetadata(new VRateLimitAttribute(VAppCoreRateLimitPolicies.Auth));
                        e.MapGet("/free", () => "ok"); // no rate limit
                    });
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        return host.GetTestServer();
    }

    [Fact]
    public async Task EndpointWithoutAttribute_NotRateLimited()
    {
        using var server = await BuildServerAsync();
        var client = server.CreateClient();

        for (int i = 0; i < 50; i++)
        {
            var res = await client.GetAsync("/free", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        }
    }

    [Fact]
    public async Task EndpointWithAttribute_LimitsRequests()
    {
        // override the default 5/min auth policy with something tight for the test
        using var server = await BuildServerAsync(o =>
        {
            o.Policies[VAppCoreRateLimitPolicies.Auth] = new RateLimitPolicy("vauth", Capacity: 3, RefillTokensPerSecond: 0);
        });
        var client = server.CreateClient();

        // First 3 succeed
        for (int i = 0; i < 3; i++)
        {
            var ok = await client.GetAsync("/auth/login", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
        }

        // 4th rejected with 429
        var rejected = await client.GetAsync("/auth/login", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After")
            || rejected.Content.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task RejectedResponse_BodyHasRateLimitedKind()
    {
        using var server = await BuildServerAsync(o =>
        {
            o.Policies[VAppCoreRateLimitPolicies.Auth] = new RateLimitPolicy("vauth", Capacity: 1, RefillTokensPerSecond: 0);
        });
        var client = server.CreateClient();

        await client.GetAsync("/auth/login", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/auth/login", TestContext.Current.CancellationToken);
        var body = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("rate_limited", body);
        Assert.Contains("server.errors.rateLimited", body);
    }

    [Fact]
    public async Task UnregisteredPolicy_Throws()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddLogging();
                    s.AddVAppCoreRateLimiting();
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<VExceptionMiddleware>();
                    app.UseRouting();
                    app.UseVRateLimiting();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/x", () => "ok").WithMetadata(new VRateLimitAttribute("not-a-real-policy"));
                    });
                });
            }).StartAsync(TestContext.Current.CancellationToken);

        using var server = host.GetTestServer();
        var client = server.CreateClient();
        var res = await client.GetAsync("/x", TestContext.Current.CancellationToken);
        // VExceptionMiddleware translates to 500 with "System Error"
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, res.StatusCode);
    }
}
