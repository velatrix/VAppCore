using Microsoft.Extensions.Logging;

namespace VAppCore;

/// <summary>
/// Built-in observer that logs every concurrency conflict at Warning level via <see cref="ILogger"/>.
/// Opt-in via <c>AddVAppCoreConcurrency(o =&gt; o.LogConflicts = true)</c>.
/// </summary>
public sealed class LoggingConcurrencyConflictObserver : IConcurrencyConflictObserver
{
    private readonly ILogger<LoggingConcurrencyConflictObserver> _log;

    public LoggingConcurrencyConflictObserver(ILogger<LoggingConcurrencyConflictObserver> log)
    {
        _log = log;
    }

    public void OnConflict(ConcurrencyConflictDetails details)
    {
        _log.LogWarning(
            details.Exception,
            "Optimistic concurrency conflict on {EntityType} {EntityId}",
            details.EntityType.Name,
            details.EntityId ?? "<unknown>");
    }
}
