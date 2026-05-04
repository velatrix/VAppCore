using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore;

/// <summary>
/// Global MVC action filter that enforces VAuthorize attributes.
/// Registered automatically by AddVAppCore().
/// </summary>
public class VAuthorizeFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attributes = context.ActionDescriptor.EndpointMetadata
            .OfType<VAuthorizeAttribute>()
            .ToList();

        if (attributes.Count == 0)
        {
            await next();
            return;
        }

        var currentUser = context.HttpContext.RequestServices
            .GetRequiredService<ICurrentUser>();

        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedError(new ErrorObject
            {
                Message = "Authentication required",
                MessageKey = "server.errors.unauthenticated"
            });

        foreach (var attr in attributes)
        {
            if (attr.ApiKey is not null)
            {
                if (currentUser.AuthenticationType != ApiKeyAuthenticationHandler.SchemeName)
                    throw new ForbiddenError(new ErrorObject
                    {
                        Message = "API key authentication required",
                        MessageKey = "api_key.required",
                        Metadata = new { permission = attr.ApiKey }
                    });
                if (!currentUser.HasPermission(attr.ApiKey))
                    throw new ForbiddenError(new ErrorObject
                    {
                        Message = $"Required permission: {attr.ApiKey}",
                        MessageKey = "permission.required",
                        Metadata = new { permission = attr.ApiKey }
                    });
                continue;
            }

            if (attr.Role is not null && !currentUser.IsInRole(attr.Role))
                throw new ForbiddenError(new ErrorObject
                {
                    Message = $"Required role: {attr.Role}",
                    MessageKey = "server.errors.forbidden"
                });

            if (attr.Permission is not null && !currentUser.HasPermission(attr.Permission))
                throw new ForbiddenError(new ErrorObject
                {
                    Message = $"Required permission: {attr.Permission}",
                    MessageKey = "server.errors.forbidden"
                });
        }

        await next();
    }
}
