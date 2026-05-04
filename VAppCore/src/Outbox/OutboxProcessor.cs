using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VAppCore;

/// <summary>
/// Background service that polls the outbox for Pending rows and dispatches them to registered
/// <see cref="IDomainEventHandler{TEvent}"/> handlers. Implements at-least-once delivery with
/// exponential backoff, dead-lettering after MaxAttempts failures, and periodic pruning of old
/// Sent rows.
/// </summary>
public class OutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OutboxOptions _opts;
    private readonly ILogger<OutboxProcessor> _log;

    public OutboxProcessor(IServiceScopeFactory scopes, IOptions<OutboxOptions> opts, ILogger<OutboxProcessor> log)
    {
        _scopes = scopes;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPrune = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Outbox dispatch pass failed");
            }

            if (DateTimeOffset.UtcNow - lastPrune > _opts.PruneInterval)
            {
                try
                {
                    await PruneAsync(stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Outbox prune pass failed");
                }
                lastPrune = DateTimeOffset.UtcNow;
            }

            try { await Task.Delay(_opts.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Dispatch a batch of Pending rows. Public + virtual so tests can drive it directly without
    /// the BackgroundService loop. In production, ExecuteAsync calls this on each poll.
    /// </summary>
    public virtual async Task ProcessOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DbContext>();

        var now = DateTimeOffset.UtcNow;
        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.Status == OutboxStatus.Pending && (m.NextRetryAt == null || m.NextRetryAt <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(_opts.BatchSize)
            .ToListAsync(ct);

        foreach (var msg in pending)
        {
            msg.Attempts++;
            try
            {
                await DispatchAsync(sp, msg, ct);
                msg.Status = OutboxStatus.Sent;
                msg.SentAt = DateTimeOffset.UtcNow;
                msg.LastError = null;
                msg.NextRetryAt = null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                msg.LastError = ex.Message;
                if (msg.Attempts >= _opts.MaxAttempts)
                {
                    msg.Status = OutboxStatus.DeadLettered;
                    _log.LogError(ex, "Outbox row {Id} dead-lettered after {Attempts} attempts", msg.Id, msg.Attempts);
                }
                else
                {
                    var delaySeconds = Math.Min(Math.Pow(2, msg.Attempts), _opts.MaxBackoff.TotalSeconds);
                    msg.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                    _log.LogWarning(ex, "Outbox row {Id} attempt {Attempts} failed; next retry at {NextRetryAt}",
                        msg.Id, msg.Attempts, msg.NextRetryAt);
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Delete Sent rows older than <see cref="OutboxOptions.RetentionDays"/>.
    /// Public + virtual so tests can drive it directly.
    /// </summary>
    public virtual async Task PruneAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_opts.RetentionDays);

        var stale = await db.Set<OutboxMessage>()
            .Where(m => m.Status == OutboxStatus.Sent && m.SentAt != null && m.SentAt < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        db.Set<OutboxMessage>().RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Pruned {Count} sent outbox rows older than {Cutoff}", stale.Count, cutoff);
    }

    private static async Task DispatchAsync(IServiceProvider sp, OutboxMessage msg, CancellationToken ct)
    {
        var eventType = Type.GetType(msg.Type)
            ?? throw new InvalidOperationException($"Cannot resolve event type '{msg.Type}'.");
        var evt = JsonSerializer.Deserialize(msg.Payload, eventType)
            ?? throw new InvalidOperationException($"Event payload for {msg.Type} deserialized to null.");

        var context = new EventContext(
            MessageId: msg.Id,
            Attempt: msg.Attempts,
            OccurredAt: msg.CreatedAt);

        var handlerInterface = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
        var handlers = sp.GetServices(handlerInterface).Where(h => h is not null).ToList();
        if (handlers.Count == 0) return; // no handlers — treat as successful dispatch (event ignored)

        var handleMethod = handlerInterface.GetMethod(nameof(IDomainEventHandler<DummyEvent>.Handle))
            ?? throw new InvalidOperationException("IDomainEventHandler<>.Handle method not found.");

        foreach (var handler in handlers)
        {
            Task task;
            try
            {
                task = (Task)handleMethod.Invoke(handler, [evt, context, ct])!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                // Handler threw synchronously inside Invoke — unwrap so callers see the real exception.
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;  // unreachable, satisfies compiler
            }
            await task;
        }
    }

    // Used only as a generic-method-resolution placeholder for the Handle() reflection lookup.
    private record DummyEvent : IDomainEvent;
}
