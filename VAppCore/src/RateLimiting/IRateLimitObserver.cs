namespace VAppCore;

/// <summary>
/// Notified when a request is rejected for exceeding its rate limit. Same pluggable pattern
/// as <see cref="IConcurrencyConflictObserver"/>: default no-op, opt-in built-in logging,
/// custom impls register via DI for Prometheus / OpenTelemetry / alerts.
/// </summary>
public interface IRateLimitObserver
{
    void OnRejected(RateLimitRejection rejection);
}

public record RateLimitRejection(
    string PolicyName,
    string PartitionKey,
    int Cost,
    TimeSpan? RetryAfter,
    string? RoutePath);

public sealed class NoOpRateLimitObserver : IRateLimitObserver
{
    public void OnRejected(RateLimitRejection rejection) { }
}
