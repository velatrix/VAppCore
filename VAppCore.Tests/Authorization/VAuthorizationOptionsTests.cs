using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace VAppCore.Tests.Authorization;

/// <summary>Registering scopes and entities, the 403 factory, and <see cref="IVAccess"/> on its own.</summary>
public sealed class VAuthorizationOptionsTests
{
    private static readonly ErrorObject NotFound = new() { Message = "x", MessageKey = "X_NOT_FOUND" };

    [Fact]
    public void A_scope_is_registered_once()
    {
        var o = new VAuthorizationOptions().AddScope<Guid>("project", "projectId", NotFound);
        Assert.Throws<InvalidOperationException>(() => o.AddScope<Guid>("project", "id", NotFound));
    }

    [Fact]
    public void An_entity_needs_a_registered_scope_with_an_id_and_a_name_of_its_own()
    {
        var o = new VAuthorizationOptions().AddScope("instance").AddScope<Guid>("project", "projectId", NotFound);
        VEntityLocator none = (_, _, _) => ValueTask.FromResult<string?>(null);

        Assert.Throws<InvalidOperationException>(() => o.AddEntity("team", "taskId", none, NotFound));
        Assert.Throws<InvalidOperationException>(() => o.AddEntity("instance", "userId", none, NotFound));
        Assert.Throws<InvalidOperationException>(() => o.AddEntity("project", "projectId", none, NotFound));

        o.AddEntity("project", "taskId", none, NotFound);
        Assert.Throws<InvalidOperationException>(() => o.AddEntity("project", "TaskId", none, NotFound));
    }

    [Fact]
    public void Ids_are_canonicalized_by_the_scopes_key_type_and_garbage_is_no_id()
    {
        var project = new VAuthorizationOptions().AddScope<Guid>("project", "projectId", NotFound).FindScope("project")!;
        var id = Guid.NewGuid();

        Assert.Equal(id.ToString(), project.Canonicalize(id.ToString("N").ToUpperInvariant()));
        Assert.Null(project.Canonicalize("not-a-guid"));
    }

    [Fact]
    public async Task A_typed_entity_locator_sees_a_malformed_id_as_no_entity()
    {
        var called = false;
        var o = new VAuthorizationOptions()
            .AddScope<Guid>("project", "projectId", NotFound)
            .AddEntity<int>("project", "itemId", (_, _, _) => { called = true; return ValueTask.FromResult<string?>(Guid.NewGuid().ToString()); }, NotFound);
        var item = o.FindScope("project")!.Entities["itemId"];

        Assert.Null(await item.LocateScopeAsync(new ServiceCollection().BuildServiceProvider(), "twelve", CancellationToken.None));
        Assert.False(called);
        Assert.NotNull(await item.LocateScopeAsync(new ServiceCollection().BuildServiceProvider(), "12", CancellationToken.None));
    }

    [Fact]
    public async Task The_403_can_be_the_apps_own()
    {
        var world = new ScopedWorld();
        using var host = await ScopedTestApp.StartAsync(world, [typeof(ProjectsController)], o => o.Forbidden = f => new ErrorObject
        {
            Message = $"Missing {string.Join("|", f.Permissions)} in {f.Scope}",
            MessageKey = "INSUFFICIENT_PERMISSIONS",
            Metadata = new { permission = f.Permissions[0] }
        });
        using var client = host.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{world.A1}/tasks/{world.TaskA1}");
        request.Headers.Add("X-Caller", "bob");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("INSUFFICIENT_PERMISSIONS", body);
        Assert.Contains($"Missing tasks.view in project:{world.A1}", body);
    }

    [Fact]
    public async Task IVAccess_outside_a_request_names_the_mistake()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ScopedWorld());
        services.AddScoped<IScopeAccessResolver, ScopedWorldResolver>();
        services.AddVAuthorization(ScopedTestApp.Register);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var access = scope.ServiceProvider.GetRequiredService<IVAccess>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => access.GetAsync(ScopedWorld.Project(Guid.NewGuid()), TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("outside a request or hub invocation", error.Message);
    }

    [Fact]
    public void AddVAuthorization_registers_its_filters_once_however_often_it_is_called()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        services.AddSignalR();
        services.AddVAuthorization(o => o.AddScope("instance"));
        services.AddVAuthorization(o => o.AddScope<Guid>("project", "projectId", NotFound));
        using var provider = services.BuildServiceProvider();

        var mvc = provider.GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Single(mvc.Filters, f => f is TypeFilterAttribute t && t.ImplementationType == typeof(VAuthorizeFilter));

        var options = provider.GetRequiredService<IOptions<VAuthorizationOptions>>().Value;
        Assert.NotNull(options.FindScope("instance"));
        Assert.NotNull(options.FindScope("project"));
    }

    [Fact]
    public void ScopeAccess_compares_permissions_like_the_flat_list()
    {
        var access = ScopeAccess.Visible(["Tasks.View"]);
        Assert.True(access.IsVisible);
        Assert.True(access.Has("tasks.view"));
        Assert.False(ScopeAccess.Hidden.IsVisible);
        Assert.False(ScopeAccess.Visible([]).Has("tasks.view"));
    }
}
