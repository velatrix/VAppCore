using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore;

/// <summary>
/// Global MVC authorization filter that enforces <see cref="VAuthorizeAttribute"/> declarations — before model
/// binding, so a refused caller never sees a validation error and a request body is not read before the check.
/// Registered by <see cref="VAuthorizationServiceCollectionExtensions.AddVAuthorization"/> (which
/// <c>AddVAppCore</c> calls). An endpoint that allows anonymous access is not checked, as with <c>[Authorize]</c>.
/// </summary>
/// <remarks>Refusals are thrown as <see cref="BaseError"/>s, for the app's exception middleware to render.</remarks>
public class VAuthorizeFilter : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        var declarations = metadata.OfType<VAuthorizeAttribute>().ToList();
        if (declarations.Count == 0 || metadata.OfType<IAllowAnonymous>().Any())
            return Task.CompletedTask;

        var http = context.HttpContext;
        var services = http.RequestServices;
        var currentUser = services.GetService<ICurrentUser>() ?? PrincipalCurrentUser.For(services, http.User);
        var routeValues = context.RouteData.Values;

        return VAuthorizationEnforcer.EnforceAsync(
            services,
            currentUser,
            http.User,
            declarations,
            name => routeValues.TryGetValue(name, out var value) && value is not null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null,
            http.RequestAborted);
    }
}
