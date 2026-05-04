namespace VAppCore;

/// <summary>
/// Opt-in optimistic-concurrency interface. Adds a <c>RowVersion</c> column that EF Core
/// includes in WHERE clauses on UPDATE and bumps on each successful update. If two requests
/// read the same row and both try to update, the second one's WHERE clause won't match
/// (because the first one bumped RowVersion) — EF throws <c>DbUpdateConcurrencyException</c>,
/// which VAppCore's <c>ConcurrencyConflictInterceptor</c> translates to a <see cref="ConflictError"/>.
/// Cross-provider — works on SQL Server, Postgres, SQLite, etc.
/// For Postgres-native concurrency without an extra column, use <see cref="IConcurrentXmin"/> instead.
/// </summary>
public interface IConcurrent
{
    byte[] RowVersion { get; set; }
}

/// <summary>
/// Postgres-native optimistic-concurrency interface. Uses Postgres's built-in <c>xmin</c>
/// system column (the transaction id of the last update on each row) as the concurrency token.
/// No migration needed for the column — Postgres maintains <c>xmin</c> automatically on every row.
/// Postgres-only. For cross-provider concurrency, use <see cref="IConcurrent"/>.
/// </summary>
public interface IConcurrentXmin
{
    uint Xmin { get; set; }
}
