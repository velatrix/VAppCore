using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class VEntityDomainEventsTests
{
    private record TestEvent(string Note) : IDomainEvent;

    [Fact]
    public void NewEntity_DomainEvents_IsEmpty()
    {
        var entity = new TestSimpleEntity();
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public void RaiseEvent_AppendsToDomainEvents()
    {
        var entity = new TestSimpleEntity();
        entity.RaiseEvent(new TestEvent("first"));
        entity.RaiseEvent(new TestEvent("second"));

        Assert.Equal(2, entity.DomainEvents.Count);
        Assert.Equal("first", ((TestEvent)entity.DomainEvents[0]).Note);
        Assert.Equal("second", ((TestEvent)entity.DomainEvents[1]).Note);
    }

    [Fact]
    public void ClearDomainEvents_EmptiesList()
    {
        var entity = new TestSimpleEntity();
        entity.RaiseEvent(new TestEvent("x"));
        entity.ClearDomainEvents();

        Assert.Empty(entity.DomainEvents);
    }
}
