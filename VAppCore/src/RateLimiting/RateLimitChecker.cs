using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// Convenience service for checking or consuming rate limits programmatically from inside
/// service code (rather than relying solely on the middleware/attribute path). Inject this
/// where you want to gate expensive operations on rate limits before doing the work.
/// </summary>
public class RateLimitChecker
{
    private readonly IRateLimitStore _store;
    private readonly IRateLimitPartitioner _partitioner;
    private readonly IHttpContextAccessor _http;
    private readonly VAppCoreRateLimitingOptions _opts;

    public RateLimitChecker(
        IRateLimitStore store,
        IRateLimitPartitioner partitioner,
        IHttpContextAccessor http,
        IOptions<VAppCoreRateLimitingOptions> opts)
    {
        _store = store;
        _partitioner = partitioner;
        _http = http;
        _opts = opts.Value;
    }

    /// <summary>
    /// Inspects whether the current caller could consume <paramref name="cost"/> tokens
    /// from the named policy without actually decrementing. Returns the result for the
    /// caller to decide what to do (show a "wait X seconds" message, etc).
    /// </summary>
    public Task<RateLimitResult> CheckAsync(string policyName, int cost = 1)
    {
        var (partition, policy) = ResolveContext(policyName);
        return _store.PeekAsync(partition, policy, cost);
    }

    /// <summary>
    /// Consumes <paramref name="cost"/> tokens from the named policy for the current caller.
    /// Returns Permitted=false if the limit is exceeded; the caller decides whether to
    /// throw <see cref="RateLimitedError"/>, return a friendly message, etc.
    /// </summary>
    public Task<RateLimitResult> ConsumeAsync(string policyName, int cost = 1)
    {
        var (partition, policy) = ResolveContext(policyName);
        return _store.TryConsumeAsync(partition, policy, cost);
    }

    private (string Partition, RateLimitPolicy Policy) ResolveContext(string policyName)
    {
        if (!_opts.Policies.TryGetValue(policyName, out var policy))
            throw new InvalidOperationException(
                $"Rate-limit policy '{policyName}' is not registered.");

        var ctx = _http.HttpContext
            ?? throw new InvalidOperationException(
                "RateLimitChecker requires an active HttpContext. Inject IHttpContextAccessor and call only inside a request scope.");

        return (_partitioner.Resolve(ctx), policy);
    }
}
