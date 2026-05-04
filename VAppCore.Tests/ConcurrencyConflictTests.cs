using Microsoft.EntityFrameworkCore;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ConcurrencyConflictTests
{
    private class CapturingObserver : IConcurrencyConflictObserver
    {
        public List<ConcurrencyConflictDetails> Captured { get; } = [];
        public void OnConflict(ConcurrencyConflictDetails details) => Captured.Add(details);
    }

    [Fact]
    public void NotifyAndThrow_ThrowsConflictError_WithExpectedMetadata()
    {
        var observers = new List<IConcurrencyConflictObserver>();
        var entityId = Guid.NewGuid();
        var inner = new DbUpdateConcurrencyException("simulated");

        var ex = Assert.Throws<ConflictError>(() =>
            ConcurrencyConflictHelper.NotifyAndThrow(observers, typeof(TestProduct), entityId, inner));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("server.errors.concurrentUpdate", ex.Context.Error?.MessageKey);
        Assert.Contains("TestProduct", ex.Context.Error?.Message);

        // Metadata is an anonymous object — reflect to read its properties
        var meta = ex.Context.Error?.Metadata;
        Assert.NotNull(meta);
        var kindProp = meta.GetType().GetProperty("kind");
        Assert.Equal("concurrent_update", kindProp?.GetValue(meta));
        var idProp = meta.GetType().GetProperty("entityId");
        Assert.Equal(entityId, idProp?.GetValue(meta));
    }

    [Fact]
    public void NotifyAndThrow_NotifiesAllObservers_BeforeThrowing()
    {
        var obs1 = new CapturingObserver();
        var obs2 = new CapturingObserver();
        var observers = new IConcurrencyConflictObserver[] { obs1, obs2 };

        Assert.Throws<ConflictError>(() =>
            ConcurrencyConflictHelper.NotifyAndThrow(observers, typeof(TestProduct), Guid.NewGuid(),
                new DbUpdateConcurrencyException("x")));

        Assert.Single(obs1.Captured);
        Assert.Single(obs2.Captured);
        Assert.Equal(typeof(TestProduct), obs1.Captured[0].EntityType);
    }

    [Fact]
    public void NotifyAndThrow_NoObservers_StillThrows()
    {
        Assert.Throws<ConflictError>(() =>
            ConcurrencyConflictHelper.NotifyAndThrow(
                Array.Empty<IConcurrencyConflictObserver>(),
                typeof(TestProduct),
                null,
                new DbUpdateConcurrencyException("x")));
    }
}
