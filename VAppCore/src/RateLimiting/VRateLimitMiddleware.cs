using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// Middleware that enforces rate limits declared via <see cref="VRateLimitAttribute"/> on the
/// matched endpoint. Must run AFTER <c>UseRouting()</c> so endpoint metadata is available.
/// Endpoints without the attribute are not rate-limited.
/// </summary>
public class VRateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public VRateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var attribute = endpoint?.Metadata.GetMetadata<VRateLimitAttribute>();
        if (attribute is null)
        {
            await _next(context);
            return;
        }

        var sp = context.RequestServices;
        var options = sp.GetRequiredService<IOptions<VAppCoreRateLimitingOptions>>().Value;

        if (!options.Policies.TryGetValue(attribute.PolicyName, out var basePolicy))
            throw new InvalidOperationException(
                $"Rate-limit policy '{attribute.PolicyName}' is not registered. " +
                $"Add it via AddVAppCoreRateLimiting(o =&gt; o.Policies[\"{attribute.PolicyName}\"] = ...).");

        // Apply tier multiplier if user matches one
        var policy = ApplyTierMultiplier(basePolicy, sp, options);

        var partitioner = sp.GetRequiredService<IRateLimitPartitioner>();
        var partitionKey = partitioner.Resolve(context);

        var store = sp.GetRequiredService<IRateLimitStore>();
        var result = await store.TryConsumeAsync(partitionKey, policy, attribute.Cost);

        if (result.Permitted)
        {
            await _next(context);
            return;
        }

        // Rejected — notify observers, then throw RateLimitedError so VExceptionMiddleware translates it
        var observers = sp.GetServices<IRateLimitObserver>();
        var rejection = new RateLimitRejection(
            policy.Name,
            partitionKey,
            attribute.Cost,
            result.RetryAfter,
            context.Request.Path);
        foreach (var observer in observers)
            observer.OnRejected(rejection);

        if (result.RetryAfter is { } retryAfter)
            context.Response.Headers["Retry-After"] = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

        throw new RateLimitedError(new ErrorObject
        {
            Message = $"Rate limit exceeded for policy '{policy.Name}'.",
            MessageKey = "server.errors.rateLimited",
            Metadata = new
            {
                kind = "rate_limited",
                policy = policy.Name,
                retryAfterSeconds = result.RetryAfter?.TotalSeconds
            }
        }, result.RetryAfter);
    }

    private static RateLimitPolicy ApplyTierMultiplier(
        RateLimitPolicy basePolicy,
        IServiceProvider sp,
        VAppCoreRateLimitingOptions options)
    {
        if (options.TierMultipliers.Count == 0) return basePolicy;

        var currentUser = sp.GetService<ICurrentUser>();
        if (currentUser is not { IsAuthenticated: true }) return basePolicy;

        double bestMultiplier = 1.0;
        foreach (var (role, multiplier) in options.TierMultipliers)
        {
            if (currentUser.IsInRole(role) && multiplier > bestMultiplier)
                bestMultiplier = multiplier;
        }

        if (bestMultiplier <= 1.0) return basePolicy;

        return basePolicy with
        {
            Capacity = bestMultiplier == double.MaxValue ? int.MaxValue : (int)(basePolicy.Capacity * bestMultiplier),
            RefillTokensPerSecond = basePolicy.RefillTokensPerSecond * bestMultiplier
        };
    }
}
