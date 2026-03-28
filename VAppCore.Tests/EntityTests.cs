namespace VAppCore.Tests;

public class EntityTests
{
    private class TestEntity : VEntity<Guid, Guid, Guid>
    {
        public string Name { get; set; } = string.Empty;
    }

    private class TestSoftEntity : VEntity<Guid, Guid, Guid>, ISoftDeletable
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public Guid? DeletedBy { get; set; }
    }

    private class TestTenantEntity : VEntity<Guid, Guid, Guid>, ITenantScoped<Guid>
    {
        public string Name { get; set; } = string.Empty;
        public Guid TenantId { get; set; }
    }

    [Fact]
    public void VEntity_HasAllAuditProperties()
    {
        var entity = new TestEntity
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedBy = Guid.NewGuid(),
            UpdatedBy = Guid.NewGuid()
        };

        Assert.NotEqual(Guid.Empty, entity.Id);
        Assert.NotEqual(default, entity.CreatedAt);
        Assert.NotEqual(Guid.Empty, entity.CreatedBy);
    }

    [Fact]
    public void ISoftDeletable_DefaultsToNotDeleted()
    {
        var entity = new TestSoftEntity();

        Assert.False(entity.IsDeleted);
        Assert.Null(entity.DeletedAt);
    }

    [Fact]
    public void ISoftDeletable_CanBeMarkedDeleted()
    {
        var entity = new TestSoftEntity
        {
            IsDeleted = true,
            DeletedAt = DateTimeOffset.UtcNow,
            DeletedBy = Guid.NewGuid()
        };

        Assert.True(entity.IsDeleted);
        Assert.NotNull(entity.DeletedAt);
        Assert.NotNull(entity.DeletedBy);
    }

    [Fact]
    public void ITenantScoped_HasTenantId()
    {
        var tenantId = Guid.NewGuid();
        var entity = new TestTenantEntity { TenantId = tenantId };

        Assert.Equal(tenantId, entity.TenantId);
    }

    [Fact]
    public void VEntity_ConvenienceBase_UsesGuids()
    {
        // VEntity (non-generic) should use Guid for all keys
        Assert.True(typeof(VEntity).BaseType!.GetGenericArguments()[0] == typeof(Guid));
        Assert.True(typeof(VEntity).BaseType!.GetGenericArguments()[1] == typeof(Guid));
        Assert.True(typeof(VEntity).BaseType!.GetGenericArguments()[2] == typeof(Guid));
    }
}
