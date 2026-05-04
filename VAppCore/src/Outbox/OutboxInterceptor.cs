using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VAppCore;

/// <summary>
/// SaveChanges interceptor that extracts <see cref="IDomainEvent"/>s from the change tracker's
/// VEntity-derived entries, serializes each to JSON, and inserts an <see cref="OutboxMessage"/>
/// row in the same SaveChanges call. Atomicity: outbox rows commit with the entity changes that
/// raised them — if the entity write rolls back, so does the event.
/// </summary>
public sealed class OutboxInterceptor : ISaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ExtractEvents(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ExtractEvents(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void ExtractEvents(DbContext? db)
    {
        if (db is null) return;

        // Snapshot the entries first — adding new OutboxMessages to ChangeTracker invalidates iteration.
        var entries = db.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            // The non-generic IVAuditedMarker access via the generic IVAudited<T> would require knowing TUserKey.
            // Instead, find the DomainEvents via reflection on the VEntity<,,> base.
            var entity = entry.Entity;
            var domainEventsProp = GetDomainEventsProperty(entity.GetType());
            if (domainEventsProp is null) continue;

            var events = (System.Collections.IEnumerable?)domainEventsProp.GetValue(entity);
            if (events is null) continue;

            var eventList = events.Cast<IDomainEvent>().ToList();
            if (eventList.Count == 0) continue;

            foreach (var evt in eventList)
            {
                var msg = new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    Type = evt.GetType().AssemblyQualifiedName ?? evt.GetType().FullName!,
                    Payload = JsonSerializer.Serialize(evt, evt.GetType(), JsonOpts),
                    Status = OutboxStatus.Pending,
                    Attempts = 0
                };
                db.Add(msg);
            }

            // Clear via the public method on the entity (works for any TKey/TUserKey/TTenantKey)
            var clearMethod = entity.GetType().GetMethod(nameof(VEntity<int, int, int>.ClearDomainEvents));
            clearMethod?.Invoke(entity, null);
        }
    }

    private static System.Reflection.PropertyInfo? GetDomainEventsProperty(Type type)
    {
        // Walk up to find VEntity<,,>.DomainEvents
        var current = type;
        while (current is not null && current != typeof(object))
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VEntity<,,>))
                return current.GetProperty(nameof(VEntity<int, int, int>.DomainEvents));
            current = current.BaseType;
        }
        return null;
    }
}
