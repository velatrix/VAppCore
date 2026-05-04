using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VAppCore;

/// <summary>
/// Intercepts EF Core's <see cref="DbUpdateConcurrencyException"/> just before it would be thrown,
/// notifies registered <see cref="IConcurrencyConflictObserver"/>s, and throws a
/// <see cref="ConflictError"/> instead — which <c>VExceptionMiddleware</c> maps to HTTP 409.
/// The thrown ConflictError carries metadata: <c>kind=concurrent_update</c>, entity type, entity id.
/// </summary>
public sealed class ConcurrencyConflictInterceptor : ISaveChangesInterceptor
{
    private readonly IEnumerable<IConcurrencyConflictObserver> _observers;

    public ConcurrencyConflictInterceptor(IEnumerable<IConcurrencyConflictObserver> observers)
    {
        _observers = observers;
    }

    public InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result)
    {
        TranslateAndThrow(eventData);
        return result;  // unreachable — TranslateAndThrow throws
    }

    public ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        TranslateAndThrow(eventData);
        return ValueTask.FromResult(result);  // unreachable
    }

    private void TranslateAndThrow(ConcurrencyExceptionEventData eventData)
    {
        var firstEntry = eventData.Entries?.FirstOrDefault();
        var entityType = firstEntry?.Entity.GetType() ?? typeof(object);
        var entityId = ConcurrencyConflictHelper.TryGetPrimaryKey(firstEntry);
        ConcurrencyConflictHelper.NotifyAndThrow(_observers, entityType, entityId, eventData.Exception);
    }
}

/// <summary>
/// Pure logic of "notify observers, throw ConflictError" — extracted so it can be unit-tested
/// without constructing EF Core's internal ConcurrencyExceptionEventData.
/// </summary>
public static class ConcurrencyConflictHelper
{
    public static void NotifyAndThrow(
        IEnumerable<IConcurrencyConflictObserver> observers,
        Type entityType,
        object? entityId,
        Exception innerException)
    {
        var details = new ConcurrencyConflictDetails(entityType, entityId, innerException);
        foreach (var observer in observers)
            observer.OnConflict(details);

        throw new ConflictError(new ErrorObject
        {
            Message = $"Optimistic concurrency conflict on {entityType.Name}{(entityId != null ? $" ({entityId})" : "")}.",
            MessageKey = "server.errors.concurrentUpdate",
            Metadata = new
            {
                kind = "concurrent_update",
                entityType = entityType.Name,
                entityId
            }
        });
    }

    public static object? TryGetPrimaryKey(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry? entry)
    {
        if (entry is null) return null;
        var keyProperties = entry.Metadata.FindPrimaryKey()?.Properties;
        if (keyProperties is null) return null;

        if (keyProperties.Count == 1)
            return entry.Property(keyProperties[0].Name).CurrentValue;

        return keyProperties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
    }
}
