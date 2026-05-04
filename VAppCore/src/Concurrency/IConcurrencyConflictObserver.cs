namespace VAppCore;

/// <summary>
/// Observed when an optimistic-concurrency conflict is detected. Implementations can log,
/// emit metrics, or surface the conflict to monitoring tools. Multiple observers can be
/// registered in DI; all are called sequentially before the <see cref="ConflictError"/> is thrown.
/// Built-in: <see cref="NoOpConcurrencyConflictObserver"/> (default), <see cref="LoggingConcurrencyConflictObserver"/>
/// (opt-in via <c>AddVAppCoreConcurrency(o =&gt; o.LogConflicts = true)</c>).
/// </summary>
public interface IConcurrencyConflictObserver
{
    void OnConflict(ConcurrencyConflictDetails details);
}

/// <summary>
/// Captured information about a concurrency conflict.
/// </summary>
public record ConcurrencyConflictDetails(
    /// <summary>The CLR type of the conflicting entity.</summary>
    Type EntityType,
    /// <summary>The primary-key value of the conflicting entity, if EF could discover it.</summary>
    object? EntityId,
    /// <summary>The original DbUpdateConcurrencyException, in case observers need lower-level info.</summary>
    Exception Exception);

/// <summary>Default observer — does nothing. Replaced when consumers configure logging or custom observers.</summary>
public sealed class NoOpConcurrencyConflictObserver : IConcurrencyConflictObserver
{
    public void OnConflict(ConcurrencyConflictDetails details) { }
}
