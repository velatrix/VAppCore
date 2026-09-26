using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VAppCore.Tests.Authorization;

/// <summary>
/// A tiny multi-tenant world for the scoped-authorization tests: organizations A and B, projects A1 and A2 (in A) and
/// B1 (in B), tasks and notes that belong to projects, and five callers — alice (A1: tasks.view, tasks.update, a; A2:
/// tasks.view), bob (A1 visible with no permissions), carol (nothing anywhere), admin (instance.admin) and an API key
/// bound to A1 (runs.read).
/// </summary>
public sealed class ScopedWorld
{
    public Guid OrgA { get; } = Guid.NewGuid();
    public Guid OrgB { get; } = Guid.NewGuid();
    public Guid A1 { get; } = Guid.NewGuid();
    public Guid A2 { get; } = Guid.NewGuid();
    public Guid B1 { get; } = Guid.NewGuid();

    public Guid TaskA1 { get; } = Guid.NewGuid();
    public Guid TaskA2 { get; } = Guid.NewGuid();
    public Guid TaskB1 { get; } = Guid.NewGuid();
    public Guid NoteA1 { get; } = Guid.NewGuid();
    public Guid NoteA2 { get; } = Guid.NewGuid();

    public Dictionary<Guid, Guid> TaskProject { get; } = new();
    public Dictionary<Guid, Guid> NoteProject { get; } = new();

    private readonly Dictionary<(string Caller, VScope Scope), ScopeAccess> _grants = new();
    private int _resolutions;

    public int Resolutions => Volatile.Read(ref _resolutions);

    public ScopedWorld()
    {
        TaskProject[TaskA1] = A1;
        TaskProject[TaskA2] = A2;
        TaskProject[TaskB1] = B1;
        NoteProject[NoteA1] = A1;
        NoteProject[NoteA2] = A2;

        Grant("alice", Project(A1), "tasks.view", "tasks.update", "a");
        Grant("alice", Project(A2), "tasks.view");
        Grant("alice", Org(OrgA), "members.view");
        Grant("bob", Project(A1));
        Grant("key", Project(A1), "runs.read");
        Grant("admin", new VScope("instance", null), "instance.admin");
    }

    public static VScope Project(Guid id) => new("project", id.ToString());
    public static VScope Org(Guid id) => new("org", id.ToString());

    public void Grant(string caller, VScope scope, params string[] permissions) =>
        _grants[(caller, scope)] = ScopeAccess.Visible(permissions);

    public ScopeAccess Resolve(ClaimsPrincipal caller, VScope scope)
    {
        Interlocked.Increment(ref _resolutions);
        var name = caller.FindFirstValue("sub");
        if (name is null)
            return ScopeAccess.Hidden;
        if (_grants.TryGetValue((name, scope), out var granted))
            return granted;
        // Every signed-in caller sees the instance; only a grant puts permissions in it.
        return scope.Type == "instance" ? ScopeAccess.Visible([]) : ScopeAccess.Hidden;
    }
}

public sealed class ScopedWorldResolver(ScopedWorld world) : IScopeAccessResolver
{
    public ValueTask<ScopeAccess> ResolveAsync(ClaimsPrincipal caller, VScope scope, CancellationToken cancellationToken) =>
        ValueTask.FromResult(world.Resolve(caller, scope));
}

/// <summary>
/// Signs the caller in from headers: <c>X-Caller</c> (the name; absent = anonymous), <c>X-Scheme</c> (the identity's
/// authentication type, default <c>"Test"</c>) and <c>X-Permission</c> (flat permission claims).
/// </summary>
public sealed class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var caller = Request.Headers["X-Caller"].ToString();
        if (string.IsNullOrEmpty(caller))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", caller) };
        claims.AddRange(Request.Headers["X-Permission"].Where(p => !string.IsNullOrEmpty(p)).Select(p => new Claim("permission", p!)));
        var type = Request.Headers["X-Scheme"].ToString() is { Length: > 0 } scheme ? scheme : SchemeName;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, type));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

public static class ScopedTestApp
{
    public static readonly ErrorObject OrgNotFound = new() { Message = "Organization not found", MessageKey = "ORG_NOT_FOUND" };
    public static readonly ErrorObject ProjectNotFound = new() { Message = "Project not found", MessageKey = "PROJECT_NOT_FOUND" };
    public static readonly ErrorObject TaskNotFound = new() { Message = "Task not found", MessageKey = "TASK_NOT_FOUND" };
    public static readonly ErrorObject NoteNotFound = new() { Message = "Note not found", MessageKey = "NOTE_NOT_FOUND" };

    /// <summary>The world's scopes and entities, as an app registers them.</summary>
    public static void Register(VAuthorizationOptions o)
    {
        o.AddScope("instance");
        o.AddScope<Guid>("org", "orgId", OrgNotFound);
        o.AddScope<Guid>("project", "projectId", ProjectNotFound);
        o.AddEntity<Guid>("project", "taskId", (sp, id, _) => Owner(sp.GetRequiredService<ScopedWorld>().TaskProject, id), TaskNotFound);
        o.AddEntity<Guid>("project", "noteId", (sp, id, _) => Owner(sp.GetRequiredService<ScopedWorld>().NoteProject, id), NoteNotFound);
    }

    private static ValueTask<string?> Owner(Dictionary<Guid, Guid> owners, Guid id) =>
        ValueTask.FromResult(owners.TryGetValue(id, out var project) ? project.ToString() : null);

    /// <summary>A test server serving exactly <paramref name="controllers"/>.</summary>
    public static async Task<IHost> StartAsync(
        ScopedWorld world,
        Type[] controllers,
        Action<VAuthorizationOptions>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddLogging();
                    s.AddRouting();
                    s.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
                    s.AddAuthorization();
                    s.AddControllers().ConfigureApplicationPartManager(parts =>
                    {
                        parts.ApplicationParts.Add(new AssemblyPart(typeof(ScopedTestApp).Assembly));
                        parts.FeatureProviders.Add(new OnlyControllers(controllers));
                    });
                    s.AddSingleton(world);
                    s.AddScoped<IScopeAccessResolver, ScopedWorldResolver>();
                    s.AddVAuthorization(o =>
                    {
                        Register(o);
                        configure?.Invoke(o);
                    });
                    services?.Invoke(s);
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<VExceptionMiddleware>();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapControllers());
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Replaces whatever controllers were discovered with exactly these.</summary>
    private sealed class OnlyControllers(Type[] controllers) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            foreach (var controller in controllers)
                feature.Controllers.Add(controller.GetTypeInfo());
        }
    }
}

public sealed class TaskBody
{
    [Required, StringLength(20, MinimumLength = 1)]
    public string? Title { get; set; }
}

[ApiController]
[Route("api/projects/{projectId:guid}")]
public class ProjectsController(IVAccess access) : ControllerBase
{
    [HttpGet]
    [VAuthorize(Scope = "project")]
    public IActionResult Get(Guid projectId) => Ok(new { projectId });

    [HttpGet("tasks/{taskId:guid}")]
    [VAuthorize(Scope = "project", Permission = "tasks.view")]
    public IActionResult GetTask(Guid projectId, Guid taskId) => Ok(new { taskId });

    [HttpPut("tasks/{taskId:guid}")]
    [VAuthorize(Scope = "project", Permission = "tasks.update")]
    public IActionResult UpdateTask(Guid projectId, Guid taskId, [FromBody] TaskBody body) => Ok(new { body.Title });

    [HttpGet("tasks/{taskId:guid}/notes/{noteId:guid}")]
    [VAuthorize(Scope = "project", Permission = "tasks.view")]
    public IActionResult GetNote(Guid projectId, Guid taskId, Guid noteId) => Ok(new { noteId });

    [HttpGet("both")]
    [VAuthorize(Scope = "project", Permission = "tasks.view")]
    [VAuthorize(Scope = "project", Permission = "a")]
    public IActionResult Both(Guid projectId) => Ok();

    [HttpGet("needs-b")]
    [VAuthorize(Scope = "project", Permission = "tasks.view")]
    [VAuthorize(Scope = "project", Permission = "b")]
    public IActionResult NeedsB(Guid projectId) => Ok();

    [HttpGet("any")]
    [VAuthorize(Scope = "project", AnyOf = new[] { "b", "a" })]
    public IActionResult Any(Guid projectId) => Ok();

    [HttpGet("any-missing")]
    [VAuthorize(Scope = "project", AnyOf = new[] { "b", "c" })]
    public IActionResult AnyMissing(Guid projectId) => Ok();

    /// <summary>A closure read: the task may be in another project, which the action checks itself.</summary>
    [HttpGet("free/{taskId:guid}")]
    [VAuthorize(Scope = "project", Permission = "tasks.view", Unbound = new[] { "taskId" })]
    public IActionResult Free(Guid projectId, Guid taskId) => Ok();

    /// <summary>Two declarations of one scope type: the route's project and the task's must be the same.</summary>
    [HttpGet("located/{taskId:guid}")]
    [VAuthorize(Scope = "project")]
    [VAuthorize(Scope = "project", ScopeFrom = "taskId", Permission = "tasks.view")]
    public IActionResult Located(Guid projectId, Guid taskId) => Ok();

    /// <summary>A permission needed for part of the request only, checked through <see cref="IVAccess"/>.</summary>
    [HttpPost("relink")]
    [VAuthorize(Scope = "project", Permission = "tasks.view")]
    public async Task<IActionResult> Relink(Guid projectId, [FromQuery] bool relink)
    {
        if (relink)
            await access.RequireAsync(ScopedWorld.Project(projectId), "tasks.update");
        return Ok();
    }
}

[ApiController]
[Route("api/tasks/{taskId:guid}")]
public class TasksController : ControllerBase
{
    [HttpGet]
    [VAuthorize(Scope = "project", ScopeFrom = "taskId", Permission = "tasks.view")]
    public IActionResult Get(Guid taskId) => Ok(new { taskId });
}

[ApiController]
[Route("api/organizations")]
public class OrganizationsController : ControllerBase
{
    [HttpGet("{id:guid}/members")]
    [VAuthorize(Scope = "org", ScopeFrom = "id", Permission = "members.view")]
    public IActionResult Members(Guid id) => Ok();
}

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    [HttpGet]
    [VAuthorize(Scope = "instance", Permission = "instance.admin")]
    public IActionResult Get() => Ok();
}

[ApiController]
[Route("api/keyed/{projectId:guid}")]
public class KeyedController : ControllerBase
{
    [HttpGet]
    [VAuthorize(Scope = "project", ApiKey = "runs.read")]
    public IActionResult Get(Guid projectId) => Ok();
}

[ApiController]
[Route("api/flat")]
public class FlatController : ControllerBase
{
    [HttpGet]
    [VAuthorize(Permission = "flat.read")]
    public IActionResult Get() => Ok();

    [HttpGet("any")]
    [VAuthorize(AnyOf = new[] { "flat.x", "flat.read" })]
    public IActionResult Any() => Ok();
}

[ApiController]
[Route("api/open")]
[VAuthorize]
public class OpenController : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get() => Ok();
}
