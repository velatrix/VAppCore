using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VAppCore;

/// <summary>
/// Blocks any ObjectResult that isn't VResponse or VPagedResponse.
/// Forces controllers to explicitly map responses — no raw entity leaks.
/// </summary>
public class VResponseFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: not null } objectResult)
        {
            var value = objectResult.Value;
            var type = value.GetType();

            if (value is VResponse response)
            {
                // Unwrap VResponse — replace with mapped data
                objectResult.Value = response.Data;
            }
            else if (IsVPagedResponse(type))
            {
                // VPagedResponse is already projected via VQueryFilter — pass through
            }
            else
            {
                throw new SystemError(new ErrorObject
                {
                    Message = $"Controller must return VResponse or VPagedResponse. Got: {type.Name}. Use VResponse.Map() to wrap your response.",
                    MessageKey = "server.errors.rawResponse"
                });
            }
        }

        await next();
    }

    private static bool IsVPagedResponse(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(VPagedResponse<>);
    }
}
