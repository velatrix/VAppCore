using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore;

/// <summary>
/// Default partitioner: rate-limits per authenticated user when ICurrentUser is present,
/// else per client IP address. The "user-x" / "ip-x" prefix prevents accidental collisions
/// between numeric user ids and string IP addresses.
/// </summary>
public sealed class DefaultRateLimitPartitioner : IRateLimitPartitioner
{
    public string Resolve(HttpContext context)
    {
        var currentUser = context.RequestServices.GetService<ICurrentUser>();
        if (currentUser is { IsAuthenticated: true })
        {
            // The non-generic ICurrentUser doesn't expose UserId — but the user may have
            // registered a generic implementation. Read from the principal directly for safety.
            var idClaim = context.User.Identity?.Name
                ?? context.User.Claims.FirstOrDefault()?.Value;
            if (!string.IsNullOrEmpty(idClaim))
                return $"user-{idClaim}";
        }
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip-{ip}";
    }
}
