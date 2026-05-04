using Microsoft.AspNetCore.Http;

namespace VAppCore;

/// <summary>
/// Resolves the partition key for a rate-limited request — the dimension along which the
/// limit is enforced (per user, per IP, etc).
/// Default <see cref="DefaultRateLimitPartitioner"/>: uses authenticated user id when present,
/// falls back to client IP. Replace via DI for custom logic (per-tenant, per-API-key, etc).
/// </summary>
public interface IRateLimitPartitioner
{
    string Resolve(HttpContext context);
}
