using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VAppCore.Tests.Authorization;

/// <summary><see cref="VAuthorizationCatalog"/> and <see cref="VAuthorizationEndpointRouteBuilderExtensions.VerifyVAuthorization"/>.</summary>
public sealed class VAuthorizationCatalogTests
{
    [Fact]
    public async Task A_fully_declared_app_describes_every_action_and_hub_method_with_no_problems()
    {
        using var host = await StartAsync([typeof(ProjectsController), typeof(TasksController)], hubs: true);
        var described = VAuthorizationCatalog.Describe(host.Services);

        var task = Assert.Single(described, d => d.Key == "GET /api/projects/{projectId}/tasks/{taskId}");
        Assert.False(task.IsHubMethod);
        Assert.Equal("Projects.GetTask", task.DisplayName);
        Assert.Equal(["projectId", "taskId"], task.Parameters);
        var declaration = Assert.Single(task.Declarations);
        Assert.Equal(("project", "tasks.view"), (declaration.Scope, declaration.Permission));

        var watch = Assert.Single(described, d => d.Key == "CatalogHub.Watch");
        Assert.True(watch.IsHubMethod);
        Assert.Equal(2, watch.Declarations.Count); // the hub's own, then the method's

        Assert.Empty(VAuthorizationCatalog.Problems(described, host.Services));
    }

    [Fact]
    public async Task Each_kind_of_incomplete_declaration_is_a_problem()
    {
        using var host = await StartAsync([typeof(IncompleteController)], hubs: true);
        var problems = VAuthorizationCatalog.Problems(VAuthorizationCatalog.Describe(host.Services), host.Services);

        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/undeclared") && p.Contains("declares no [VAuthorize]"));
        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/both") && p.Contains("also allows anonymous access"));
        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/unknown-scope") && p.Contains("scope 'team', which is not registered"));
        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/{projectId}/typo") && p.Contains("from 'projectID2'"));
        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/{projectId}/unbound/{widgetId}") && p.Contains("route value 'widgetId' is not bound"));
        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/{projectId}/stale") && p.Contains("Unbound names 'gone'"));
        Assert.Contains(problems, p => p.StartsWith("GET /api/incomplete/singleton") && p.Contains("that scope has no id"));
        Assert.Contains(problems, p => p.StartsWith("IncompleteHub.Undeclared") && p.Contains("declares no [VAuthorize]"));

        // Declared as intended: no problem of their own.
        Assert.DoesNotContain(problems, p => p.StartsWith("GET /api/incomplete/{projectId}/declared-unbound/"));
        Assert.DoesNotContain(problems, p => p.StartsWith("GET /api/incomplete/anonymous"));
    }

    [Fact]
    public async Task Verify_refuses_to_start_an_app_with_problems_and_lists_them()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync([typeof(IncompleteController)], hubs: false, verify: true));
        Assert.Contains("VAuthorize declarations are incomplete", error.Message);
        Assert.Contains("GET /api/incomplete/undeclared", error.Message);
    }

    [Fact]
    public async Task Verify_passes_a_complete_app()
    {
        using var host = await StartAsync([typeof(ProjectsController), typeof(TasksController)], hubs: true, verify: true);
        Assert.NotNull(host);
    }

    [Fact]
    public async Task Verify_reports_scoped_declarations_with_no_resolver()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync([typeof(TasksController)], hubs: false, verify: true, resolver: false));
        Assert.Contains("no IScopeAccessResolver is registered", error.Message);
    }

    [Theory]
    [InlineData("api/Project/{projectId:guid}/runs/{runId:guid}", "/api/project/{projectId}/runs/{runId}")]
    [InlineData("/api/{*path}", "/api/{path}")]
    [InlineData("api/items/{id?}", "/api/items/{id}")]
    [InlineData("api/items/{page=1}", "/api/items/{page}")]
    public void Normalize_drops_constraints_and_lower_cases_literals(string raw, string expected) =>
        Assert.Equal(expected, VAuthorizationCatalog.Normalize(raw));

    private static async Task<IHost> StartAsync(Type[] controllers, bool hubs, bool verify = false, bool resolver = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(typeof(ScopedTestApp).Assembly));
            parts.FeatureProviders.Add(new Only(controllers));
        });
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(new ScopedWorld());
        if (resolver)
            builder.Services.AddScoped<IScopeAccessResolver, ScopedWorldResolver>();
        builder.Services.AddVAuthorization(o =>
        {
            ScopedTestApp.Register(o);
        });

        var app = builder.Build();
        try
        {
            app.MapControllers();
            if (hubs)
            {
                app.MapHub<CatalogHub>("/hubs/catalog");
                if (controllers.Contains(typeof(IncompleteController)))
                    app.MapHub<IncompleteHub>("/hubs/incomplete");
            }
            if (verify)
                app.VerifyVAuthorization();

            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private sealed class Only(Type[] controllers) : Microsoft.AspNetCore.Mvc.ApplicationParts.IApplicationFeatureProvider<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature>
    {
        public void PopulateFeature(
            IEnumerable<Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPart> parts,
            Microsoft.AspNetCore.Mvc.Controllers.ControllerFeature feature)
        {
            feature.Controllers.Clear();
            foreach (var controller in controllers)
                feature.Controllers.Add(System.Reflection.IntrospectionExtensions.GetTypeInfo(controller));
        }
    }
}

[VAuthorize]
public sealed class CatalogHub : Hub
{
    [VAuthorize(Scope = "project", ScopeFrom = "taskId", Permission = "tasks.view")]
    public Task Watch(string taskId) => Task.CompletedTask;

    public Task Leave() => Task.CompletedTask;
}

public sealed class IncompleteHub : Hub
{
    public Task Undeclared() => Task.CompletedTask;
}

[ApiController]
[Route("api/incomplete")]
public class IncompleteController : ControllerBase
{
    [HttpGet("undeclared")]
    public IActionResult Undeclared() => Ok();

    [HttpGet("both")]
    [AllowAnonymous]
    [VAuthorize]
    public IActionResult Both() => Ok();

    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult Anonymous() => Ok();

    [HttpGet("unknown-scope")]
    [VAuthorize(Scope = "team")]
    public IActionResult UnknownScope() => Ok();

    [HttpGet("{projectId:guid}/typo")]
    [VAuthorize(Scope = "project", ScopeFrom = "projectID2")]
    public IActionResult Typo(Guid projectId) => Ok();

    [HttpGet("{projectId:guid}/unbound/{widgetId:guid}")]
    [VAuthorize(Scope = "project")]
    public IActionResult UnboundWidget(Guid projectId, Guid widgetId) => Ok();

    [HttpGet("{projectId:guid}/declared-unbound/{widgetId:guid}")]
    [VAuthorize(Scope = "project", Unbound = new[] { "widgetId" })]
    public IActionResult DeclaredUnbound(Guid projectId, Guid widgetId) => Ok();

    [HttpGet("{projectId:guid}/stale")]
    [VAuthorize(Scope = "project", Unbound = new[] { "gone" })]
    public IActionResult Stale(Guid projectId) => Ok();

    [HttpGet("singleton")]
    [VAuthorize(Scope = "instance", ScopeFrom = "id")]
    public IActionResult Singleton() => Ok();
}
