namespace VAppCore.Tests;

public class VPagedResponseTests
{
    [Theory]
    [InlineData(100, 10, 10)]
    [InlineData(101, 10, 11)]
    [InlineData(0, 10, 0)]
    [InlineData(1, 10, 1)]
    [InlineData(10, 10, 1)]
    [InlineData(55, 20, 3)]
    public void TotalPages_CalculatesCorrectly(int totalItems, int size, int expectedPages)
    {
        var response = new VPagedResponse<object>
        {
            TotalItems = totalItems,
            Size = size
        };

        Assert.Equal(expectedPages, response.TotalPages);
    }

    [Fact]
    public void Items_DefaultsToEmptyList()
    {
        var response = new VPagedResponse<string>();

        Assert.NotNull(response.Items);
        Assert.Empty(response.Items);
    }
}
