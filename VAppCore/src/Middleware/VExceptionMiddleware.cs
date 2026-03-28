using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace VAppCore;

/// <summary>
/// Global exception-handling middleware. Catches all exceptions and returns
/// consistent JSON error responses. Works for both MVC and minimal APIs.
/// </summary>
public class VExceptionMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public VExceptionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BaseError ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(ex.Context, JsonOptions));
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new ErrorContext
            {
                Title = "System Error",
                TitleKey = "server.errors.system",
                Error = new ErrorObject
                {
                    Message = ex.Message,
                    MessageKey = "server.errors.system"
                }
            }, JsonOptions));
        }
    }
}
