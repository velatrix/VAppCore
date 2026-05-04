using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore.Tests;

public class VAuthorizeFilterApiKeyTests
{
    private static ActionExecutingContext BuildContext(VAuthorizeAttribute attr, ICurrentUser user)
    {
        var http = new DefaultHttpContext();
        var sp = new ServiceCollection().AddSingleton(user).BuildServiceProvider();
        http.RequestServices = sp;

        var ad = new ActionDescriptor
        {
            EndpointMetadata = new[] { (object)attr }
        };
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(http, new RouteData(), ad);
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private sealed class FakeUser : ICurrentUser
    {
        public bool IsAuthenticated { get; init; }
        public string? AuthenticationType { get; init; }
        public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

        public bool IsInRole(string role) => false;
        public bool HasPermission(string permission) => Permissions.Contains(permission);
    }

    [Fact]
    public async Task ApiKeyAttribute_UnauthenticatedCaller_Throws401()
    {
        var attr = new VAuthorizeAttribute { ApiKey = "matches.report" };
        var user = new FakeUser { IsAuthenticated = false };
        var ctx = BuildContext(attr, user);
        var filter = new VAuthorizeFilter();

        await Assert.ThrowsAsync<UnauthorizedError>(() =>
            filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!)));
    }

    [Fact]
    public async Task ApiKeyAttribute_UserCookieCaller_Throws403_NotApiKeyScheme()
    {
        var attr = new VAuthorizeAttribute { ApiKey = "matches.report" };
        var user = new FakeUser
        {
            IsAuthenticated = true,
            AuthenticationType = "Cookies",
            Permissions = new[] { "matches.report" }
        };
        var ctx = BuildContext(attr, user);
        var filter = new VAuthorizeFilter();

        var ex = await Assert.ThrowsAsync<ForbiddenError>(() =>
            filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!)));
        Assert.Equal("api_key.required", ex.Context.Error.MessageKey);
    }

    [Fact]
    public async Task ApiKeyAttribute_ApiKeyCaller_MissingPermission_Throws403_PermissionRequired()
    {
        var attr = new VAuthorizeAttribute { ApiKey = "matches.report" };
        var user = new FakeUser
        {
            IsAuthenticated = true,
            AuthenticationType = "ApiKey",
            Permissions = new[] { "matches.read" }
        };
        var ctx = BuildContext(attr, user);
        var filter = new VAuthorizeFilter();

        var ex = await Assert.ThrowsAsync<ForbiddenError>(() =>
            filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!)));
        Assert.Equal("permission.required", ex.Context.Error.MessageKey);
    }

    [Fact]
    public async Task ApiKeyAttribute_ApiKeyCaller_HasPermission_PassesThrough()
    {
        var attr = new VAuthorizeAttribute { ApiKey = "matches.report" };
        var user = new FakeUser
        {
            IsAuthenticated = true,
            AuthenticationType = "ApiKey",
            Permissions = new[] { "matches.report" }
        };
        var ctx = BuildContext(attr, user);
        var filter = new VAuthorizeFilter();

        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, () => { nextCalled = true; return Task.FromResult<ActionExecutedContext>(null!); });
        Assert.True(nextCalled);
    }
}
