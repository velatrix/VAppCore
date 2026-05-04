using Microsoft.EntityFrameworkCore;

namespace VAppCore;

public static class ConcurrencyExtensions
{
    /// <summary>
    /// Re-runs <paramref name="operation"/> if it throws <see cref="ConflictError"/>
    /// (translated from a concurrency conflict). Between attempts, the change tracker is cleared
    /// so the operation re-reads fresh data. Pattern is for self-contained read-modify-save
    /// operations where retrying with the latest state is the right answer (counter increments,
    /// score updates).
    /// Throws the final <see cref="ConflictError"/> if all attempts fail.
    /// </summary>
    public static async Task<T> RetryOnConflictAsync<T>(
        this DbContext db,
        Func<Task<T>> operation,
        int maxAttempts = 3,
        Action<int, ConflictError>? onRetry = null)
    {
        ConflictError? lastError = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (ConflictError ex) when (attempt < maxAttempts)
            {
                lastError = ex;
                onRetry?.Invoke(attempt, ex);
                db.ChangeTracker.Clear();
            }
        }
        throw lastError ?? throw new InvalidOperationException("Unreachable");
    }

    /// <summary>Void overload for operations that don't return a value.</summary>
    public static Task RetryOnConflictAsync(
        this DbContext db,
        Func<Task> operation,
        int maxAttempts = 3,
        Action<int, ConflictError>? onRetry = null)
        => db.RetryOnConflictAsync<object?>(async () => { await operation(); return null; }, maxAttempts, onRetry);

    /// <summary>
    /// Saves with "client wins" conflict resolution: if the save throws
    /// <see cref="DbUpdateConcurrencyException"/>, refresh original values from the DB so the
    /// next save sees no conflict, then retry. Effectively last-write-wins, on purpose.
    /// Use sparingly — admin overrides, force-overwrites, recovery flows.
    /// </summary>
    public static async Task<int> SaveChangesIgnoreConcurrencyAsync(
        this DbContext db,
        CancellationToken ct = default)
    {
        while (true)
        {
            try
            {
                return await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    var dbValues = await entry.GetDatabaseValuesAsync(ct);
                    if (dbValues != null)
                        entry.OriginalValues.SetValues(dbValues);
                    else
                        // Row was deleted from DB by someone else — accept that we lose the update too
                        entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
                }
            }
        }
    }
}
