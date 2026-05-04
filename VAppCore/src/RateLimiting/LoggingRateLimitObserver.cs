using Microsoft.Extensions.Logging;

namespace VAppCore;

public sealed class LoggingRateLimitObserver : IRateLimitObserver
{
    private readonly ILogger<LoggingRateLimitObserver> _log;

    public LoggingRateLimitObserver(ILogger<LoggingRateLimitObserver> log) => _log = log;

    public void OnRejected(RateLimitRejection r)
    {
        _log.LogWarning(
            "Rate limit rejection: policy={Policy} partition={Partition} cost={Cost} retryAfter={RetryAfter}s route={Route}",
            r.PolicyName,
            r.PartitionKey,
            r.Cost,
            r.RetryAfter?.TotalSeconds,
            r.RoutePath ?? "<unknown>");
    }
}
