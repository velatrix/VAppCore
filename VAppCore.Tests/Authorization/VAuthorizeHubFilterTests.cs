using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore.Tests.Authorization;

/// <summary><see cref="VAuthorizeHubFilter"/>: the same rules as the MVC filter, with scopes read from parameters.</summary>
public sealed class VAuthorizeHubFilterTests : IDisposable
{
    private readonly ScopedWorld _world = new();
    private readonly ServiceProvider _services;

    public VAuthorizeHubFilterTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_world);
        services.AddScoped<IScopeAccessResolver, ScopedWorldResolver>();
        services.AddVAuthorization(ScopedTestApp.Register);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task A_method_whose_scope_is_located_through_an_argument_passes_for_the_permission_there()
    {
        var (called, refusal) = await Invoke(nameof(TasksHub.Watch), "alice", _world.TaskA1.ToString());
        Assert.True(called);
        Assert.Null(refusal);
    }

    [Fact]
    public async Task A_hidden_scope_reached_through_an_argument_is_refused_as_the_entity_not_found()
    {
        var (called, refusal) = await Invoke(nameof(TasksHub.Watch), "alice", _world.TaskB1.ToString());
        var (_, never) = await Invoke(nameof(TasksHub.Watch), "alice", Guid.NewGuid().ToString());

        Assert.False(called);
        Assert.Equal("TASK_NOT_FOUND", refusal);
        Assert.Equal(never, refusal);
    }

    [Fact]
    public async Task A_missing_permission_is_refused_with_its_message_key()
    {
        var (called, refusal) = await Invoke(nameof(TasksHub.Watch), "bob", _world.TaskA1.ToString());
        Assert.False(called);
        Assert.Equal("permission.required", refusal);
    }

    [Fact]
    public async Task A_parameter_can_carry_the_scopes_own_id()
    {
        Assert.Null((await Invoke(nameof(TasksHub.JoinOrganization), "alice", _world.OrgA.ToString())).Refusal);
        Assert.Equal("ORG_NOT_FOUND", (await Invoke(nameof(TasksHub.JoinOrganization), "alice", _world.OrgB.ToString())).Refusal);
    }

    [Fact]
    public async Task The_hubs_own_declaration_applies_to_every_method()
    {
        Assert.Null((await Invoke(nameof(TasksHub.Leave), "carol")).Refusal);
        Assert.Equal("server.errors.unauthenticated", (await Invoke(nameof(TasksHub.Leave), caller: null)).Refusal);
    }

    [Fact]
    public async Task A_method_with_no_declaration_of_its_own_on_an_undeclared_hub_is_not_checked()
    {
        var (called, refusal) = await Invoke(nameof(OpenHub.Ping), caller: null, hub: new OpenHub());
        Assert.True(called);
        Assert.Null(refusal);
    }

    private async Task<(bool Called, string? Refusal)> Invoke(string method, string? caller, params object?[] arguments) =>
        await Invoke(method, caller, new TasksHub(), arguments);

    private async Task<(bool Called, string? Refusal)> Invoke(string method, string? caller, Hub hub, params object?[] arguments)
    {
        await using var scope = _services.CreateAsyncScope();
        var principal = caller is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", caller)], "Test"));
        var context = new HubInvocationContext(
            new FakeCallerContext(principal), scope.ServiceProvider, hub, hub.GetType().GetMethod(method)!, arguments);

        var called = false;
        try
        {
            await new VAuthorizeHubFilter().InvokeMethodAsync(context, _ =>
            {
                called = true;
                return ValueTask.FromResult<object?>(null);
            });
            return (called, null);
        }
        catch (HubException refused)
        {
            return (called, refused.Message);
        }
    }

    [VAuthorize]
    public sealed class TasksHub : Hub
    {
        [VAuthorize(Scope = "project", ScopeFrom = "taskId", Permission = "tasks.view")]
        public Task Watch(string taskId) => Task.CompletedTask;

        [VAuthorize(Scope = "org")]
        public Task JoinOrganization(string orgId) => Task.CompletedTask;

        public Task Leave() => Task.CompletedTask;
    }

    public sealed class OpenHub : Hub
    {
        public Task Ping() => Task.CompletedTask;
    }

    private sealed class FakeCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "test";
        public override string? UserIdentifier => user.FindFirstValue("sub");
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
