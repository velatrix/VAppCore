using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class ConcurrencyConfigurationTests
{
    private static TestDbContext BuildContext()
    {
        var (db, _) = TestFactory.CreateDbContext();
        return db;
    }

    [Fact]
    public void IConcurrent_Entity_HasIsRowVersionConfigured()
    {
        using var db = BuildContext();
        var rowVersionProp = db.Model
            .FindEntityType(typeof(TestConcurrentEntity))!
            .FindProperty(nameof(TestConcurrentEntity.RowVersion))!;

        Assert.True(rowVersionProp.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, rowVersionProp.ValueGenerated);
    }

    [Fact]
    public void NonConcurrent_Entity_HasNoConcurrencyToken()
    {
        using var db = BuildContext();
        var nameProp = db.Model
            .FindEntityType(typeof(TestSimpleEntity))!
            .FindProperty(nameof(TestSimpleEntity.Name))!;

        Assert.False(nameProp.IsConcurrencyToken);
    }
}
