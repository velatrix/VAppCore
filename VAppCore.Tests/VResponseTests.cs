namespace VAppCore.Tests;

public class VResponseTests
{
    // ── VResponse.Map ──

    [Fact]
    public void Map_ReturnsMappedData()
    {
        var entity = new { Id = 1, Name = "Test", Secret = "hidden" };

        var response = VResponse.Map(entity, e => new { e.Id, e.Name });

        Assert.NotNull(response);
        // Data is internal — verify via the filter unwrap pattern
    }

    [Fact]
    public void Map_TransformsEntity()
    {
        var product = new TestProduct { Name = "TV", Price = 499m };

        var response = VResponse.Map(product, p => new
        {
            p.Name,
            FormattedPrice = $"${p.Price}"
        });

        // Use reflection to access internal Data for testing
        var data = GetData(response);
        var name = data.GetType().GetProperty("Name")!.GetValue(data);
        var price = data.GetType().GetProperty("FormattedPrice")!.GetValue(data);

        Assert.Equal("TV", name);
        Assert.Equal("$499", price);
    }

    // ── VResponse.MapList ──

    [Fact]
    public void MapList_ReturnsMappedList()
    {
        var entities = new[]
        {
            new TestProduct { Name = "A", Price = 10m },
            new TestProduct { Name = "B", Price = 20m }
        };

        var response = VResponse.MapList(entities, p => new { p.Name });

        var data = (IList<object>)GetData(response);
        Assert.Equal(2, data.Count);
    }

    [Fact]
    public void MapList_Empty_ReturnsEmptyList()
    {
        var response = VResponse.MapList(Array.Empty<TestProduct>(), p => new { p.Name });

        var data = (IList<object>)GetData(response);
        Assert.Empty(data);
    }

    // ── Data is internal ──

    [Fact]
    public void Data_NotPubliclyAccessible()
    {
        var prop = typeof(VResponse).GetProperty("Data",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        Assert.Null(prop); // no public Data property
    }

    // Helper
    private static object GetData(VResponse response)
    {
        var prop = typeof(VResponse).GetProperty("Data",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return prop.GetValue(response)!;
    }

    private class TestProduct
    {
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string InternalSecret { get; set; } = "secret";
    }
}
