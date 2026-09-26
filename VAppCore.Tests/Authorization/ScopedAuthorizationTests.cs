using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace VAppCore.Tests.Authorization;

/// <summary>
/// <see cref="VAuthorizeAttribute.Scope"/> end to end: the MVC authorization filter against real routing, model
/// binding and the exception middleware.
/// </summary>
public sealed class ScopedAuthorizationTests : IAsyncLifetime
{
    private readonly ScopedWorld _world = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    private static readonly Type[] Controllers =
    [
        typeof(ProjectsController), typeof(TasksController), typeof(OrganizationsController),
        typeof(AdminController), typeof(KeyedController), typeof(FlatController), typeof(OpenController),
    ];

    public async ValueTask InitializeAsync()
    {
        _host = await ScopedTestApp.StartAsync(_world, Controllers);
        _client = _host.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ---- 401, 404, 403, pass ----

    [Fact]
    public async Task Anonymous_caller_is_401()
    {
        var answer = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}", caller: null);
        Assert.Equal(HttpStatusCode.Unauthorized, answer.Status);
        Assert.Equal("server.errors.unauthenticated", answer.MessageKey);
    }

    [Fact]
    public async Task A_caller_who_may_not_see_the_scope_gets_the_scopes_404_exactly_as_for_an_id_never_issued()
    {
        var hidden = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}", "carol");
        var never = await Send(HttpMethod.Get, $"/api/projects/{Guid.NewGuid()}", "carol");

        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal("PROJECT_NOT_FOUND", hidden.MessageKey);
        Assert.Equal(never.Body, hidden.Body);
    }

    [Fact]
    public async Task A_caller_who_sees_the_scope_but_lacks_the_permission_gets_403_naming_it()
    {
        var answer = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "bob");

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal("permission.required", answer.MessageKey);
        Assert.Equal("tasks.view", answer.Json.GetProperty("error").GetProperty("metadata").GetProperty("permission").GetString());
    }

    [Fact]
    public async Task Membership_alone_passes_a_declaration_without_a_permission()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/projects/{_world.A1}", "bob")).Status);
    }

    [Fact]
    public async Task The_permission_in_the_scope_passes()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "alice")).Status);
    }

    [Fact]
    public async Task A_permission_held_in_another_scope_does_not_count()
    {
        // alice holds tasks.update in A1 only.
        var answer = await Send(HttpMethod.Put, $"/api/projects/{_world.A2}/tasks/{_world.TaskA2}", "alice", new { title = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
    }

    // ---- Binding route ids to the scope ----

    [Fact]
    public async Task A_route_id_of_another_scope_gets_the_entitys_404_exactly_as_for_an_id_never_issued()
    {
        var foreign = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskA2}", "alice");
        var otherTenant = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskB1}", "alice");
        var never = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{Guid.NewGuid()}", "alice");

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal("TASK_NOT_FOUND", foreign.MessageKey);
        Assert.Equal(never.Body, foreign.Body);
        Assert.Equal(never.Body, otherTenant.Body);
    }

    [Fact]
    public async Task Every_registered_route_id_is_bound()
    {
        var foreignNote = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}/notes/{_world.NoteA2}", "alice");
        Assert.Equal(HttpStatusCode.NotFound, foreignNote.Status);
        Assert.Equal("NOTE_NOT_FOUND", foreignNote.MessageKey);

        var own = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}/notes/{_world.NoteA1}", "alice");
        Assert.Equal(HttpStatusCode.OK, own.Status);
    }

    [Fact]
    public async Task A_caller_refused_for_a_permission_learns_nothing_about_the_ids()
    {
        var foreign = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskB1}", "bob");
        var own = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "bob");

        Assert.Equal(HttpStatusCode.Forbidden, foreign.Status);
        Assert.Equal(own.Body, foreign.Body);
    }

    [Fact]
    public async Task An_unbound_route_id_is_left_to_the_action()
    {
        var answer = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/free/{_world.TaskA2}", "alice");
        Assert.Equal(HttpStatusCode.OK, answer.Status);
    }

    [Fact]
    public async Task Ids_are_compared_in_their_canonical_form()
    {
        var answer = await Send(HttpMethod.Get,
            $"/api/projects/{_world.A1.ToString("N").ToUpperInvariant()}/tasks/{_world.TaskA1:B}", "alice");
        Assert.Equal(HttpStatusCode.OK, answer.Status);
    }

    // ---- A scope reached through an entity ----

    [Fact]
    public async Task A_scope_located_through_an_entity_answers_the_entitys_404_when_hidden()
    {
        var hidden = await Send(HttpMethod.Get, $"/api/tasks/{_world.TaskB1}", "alice");
        var never = await Send(HttpMethod.Get, $"/api/tasks/{Guid.NewGuid()}", "alice");

        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal("TASK_NOT_FOUND", hidden.MessageKey);
        Assert.Equal(never.Body, hidden.Body);
    }

    [Fact]
    public async Task A_scope_located_through_an_entity_checks_the_permission_there()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/tasks/{_world.TaskA2}", "alice")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"/api/tasks/{_world.TaskA1}", "bob")).Status);
    }

    [Fact]
    public async Task Two_declarations_of_one_scope_type_must_name_the_same_scope()
    {
        // alice may see both A1 and A2 and view tasks in both — but A2's task is not under A1's route.
        var crossed = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/located/{_world.TaskA2}", "alice");
        Assert.Equal(HttpStatusCode.NotFound, crossed.Status);
        Assert.Equal("TASK_NOT_FOUND", crossed.MessageKey);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/located/{_world.TaskA1}", "alice")).Status);
    }

    [Fact]
    public async Task ScopeFrom_can_name_another_route_value_that_holds_the_scopes_id()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/organizations/{_world.OrgA}/members", "alice")).Status);
        var hidden = await Send(HttpMethod.Get, $"/api/organizations/{_world.OrgB}/members", "alice");
        Assert.Equal(HttpStatusCode.NotFound, hidden.Status);
        Assert.Equal("ORG_NOT_FOUND", hidden.MessageKey);
    }

    // ---- Several declarations ----

    [Fact]
    public async Task Stacked_declarations_all_apply()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/both", "alice")).Status);

        var missing = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/needs-b", "alice");
        Assert.Equal(HttpStatusCode.Forbidden, missing.Status);
        Assert.Equal("b", missing.Json.GetProperty("error").GetProperty("metadata").GetProperty("permission").GetString());
    }

    [Fact]
    public async Task AnyOf_needs_one_of_its_permissions()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/any", "alice")).Status);

        var missing = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/any-missing", "alice");
        Assert.Equal(HttpStatusCode.Forbidden, missing.Status);
        var anyOf = missing.Json.GetProperty("error").GetProperty("metadata").GetProperty("anyOf");
        Assert.Equal(["b", "c"], anyOf.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_resolver_is_asked_once_per_scope_per_request()
    {
        var before = _world.Resolutions;
        var answer = await Send(HttpMethod.Get, $"/api/projects/{_world.A1}/both", "alice");

        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.Equal(1, _world.Resolutions - before);
    }

    // ---- Before model binding ----

    [Fact]
    public async Task A_refused_caller_never_sees_a_validation_error()
    {
        var invalid = new { title = "" };

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "carol", invalid)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "bob", invalid)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "alice", invalid)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/api/projects/{_world.A1}/tasks/{_world.TaskA1}", "alice", new { title = "ok" })).Status);
    }

    // ---- IVAccess ----

    [Fact]
    public async Task IVAccess_refuses_like_a_declaration_and_shares_its_memo()
    {
        var before = _world.Resolutions;

        // alice may view tasks in A2 (the declaration passes) but not update them (IVAccess refuses).
        var partial = await Send(HttpMethod.Post, $"/api/projects/{_world.A2}/relink?relink=true", "alice");
        Assert.Equal(HttpStatusCode.Forbidden, partial.Status);
        Assert.Equal("tasks.update", partial.Json.GetProperty("error").GetProperty("metadata").GetProperty("permission").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/projects/{_world.A2}/relink?relink=false", "alice")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/projects/{_world.A1}/relink?relink=true", "alice")).Status);

        // Three requests, one scope each: the filter's resolution served IVAccess too.
        Assert.Equal(3, _world.Resolutions - before);
    }

    // ---- Singleton scopes, API keys, flat permissions, anonymous ----

    [Fact]
    public async Task A_singleton_scope_is_visible_and_its_permission_is_403_when_missing()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/admin", "admin")).Status);
        var answer = await Send(HttpMethod.Get, "/api/admin", "alice");
        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal("instance.admin", answer.Json.GetProperty("error").GetProperty("metadata").GetProperty("permission").GetString());
    }

    [Fact]
    public async Task A_scoped_ApiKey_declaration_needs_the_api_key_scheme_and_the_key_in_the_scope()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/keyed/{_world.A1}", "key", scheme: "ApiKey")).Status);

        var user = await Send(HttpMethod.Get, $"/api/keyed/{_world.A1}", "alice");
        Assert.Equal(HttpStatusCode.Forbidden, user.Status);
        Assert.Equal("api_key.required", user.MessageKey);

        var elsewhere = await Send(HttpMethod.Get, $"/api/keyed/{_world.A2}", "key", scheme: "ApiKey");
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.Status);
    }

    [Fact]
    public async Task Unscoped_declarations_read_the_flat_permission_list_as_before()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/flat", "carol", permissions: ["flat.read"])).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/flat/any", "carol", permissions: ["flat.read"])).Status);

        var refused = await Send(HttpMethod.Get, "/api/flat", "carol");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("server.errors.forbidden", refused.MessageKey);
    }

    [Fact]
    public async Task Allowing_anonymous_access_wins_as_with_Authorize()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/open", caller: null)).Status);
    }

    // ---- Plumbing ----

    private sealed record Answer(HttpStatusCode Status, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        public string? MessageKey =>
            Body.Length > 0 && Json.TryGetProperty("error", out var error) && error.TryGetProperty("messageKey", out var key)
                ? key.GetString()
                : null;
    }

    private async Task<Answer> Send(
        HttpMethod method, string url, string? caller, object? json = null, string? scheme = null, string[]? permissions = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (caller is not null)
            request.Headers.Add("X-Caller", caller);
        if (scheme is not null)
            request.Headers.Add("X-Scheme", scheme);
        foreach (var permission in permissions ?? [])
            request.Headers.Add("X-Permission", permission);
        if (json is not null)
            request.Content = JsonContent.Create(json);

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
