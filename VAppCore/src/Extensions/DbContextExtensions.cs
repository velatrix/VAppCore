using Microsoft.EntityFrameworkCore;

namespace VAppCore;

public static class DbContextExtensions
{
    /// <summary>
    /// Wraps the action in a transaction. If <paramref name="db"/> is already inside
    /// a transaction, the action runs within that transaction (no nesting). Rolls back
    /// and rethrows on exception, commits otherwise.
    /// </summary>
    public static async Task<T> TransactionAsync<T>(this DbContext db, Func<Task<T>> action)
    {
        if (db.Database.CurrentTransaction is not null)
            return await action();

        using var trx = await db.Database.BeginTransactionAsync();
        try
        {
            var result = await action();
            await trx.CommitAsync();
            return result;
        }
        catch
        {
            await trx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Wraps the action in a transaction. If <paramref name="db"/> is already inside
    /// a transaction, the action runs within that transaction (no nesting). Rolls back
    /// and rethrows on exception, commits otherwise.
    /// </summary>
    public static async Task TransactionAsync(this DbContext db, Func<Task> action)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await action();
            return;
        }

        using var trx = await db.Database.BeginTransactionAsync();
        try
        {
            await action();
            await trx.CommitAsync();
        }
        catch
        {
            await trx.RollbackAsync();
            throw;
        }
    }
}
