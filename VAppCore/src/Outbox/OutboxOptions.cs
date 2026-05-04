namespace VAppCore;

/// <summary>
/// Configuration for the outbox processor. Defaults match a typical production-ready setup;
/// adjust per-deployment as needed.
/// </summary>
public class OutboxOptions
{
    /// <summary>How often the processor wakes up to look for Pending rows. Default: 2 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Max number of Pending rows fetched per poll. Default: 50.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Maximum dispatch attempts before a row moves to <see cref="OutboxStatus.DeadLettered"/>.
    /// Default: 10.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// Exponential backoff base — retry delay = min(2^Attempts seconds, MaxBackoff).
    /// Default base is implicit (2^attempts); only the cap is configurable.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the processor runs the prune pass to delete old Sent rows. Default: 1 hour.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Sent rows older than this are deleted by the prune pass. Default: 30 days.</summary>
    public int RetentionDays { get; set; } = 30;
}
