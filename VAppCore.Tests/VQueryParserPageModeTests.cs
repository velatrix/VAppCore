using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace VAppCore.Tests;

/// <summary>
/// Page mode (<c>?page=N</c> through <see cref="VQueryParser.ApplyWithProjectionAsync{T}"/>) shares the
/// unified <see cref="VPagedResponse{T}"/> with cursor mode, so <c>HasMore</c> has to mean the same thing
/// in both: there are rows beyond this page. It used to be left at its default in page mode, which
/// reported every page as the last one while <c>TotalPages</c> said otherwise.
/// </summary>
public class VQueryParserPageModeTests
{
    [Fact]
    public async Task HasMore_is_true_while_rows_remain_and_false_on_the_last_page()
    {
        await using var db = CreateDb();
        for (var i = 0; i < 5; i++)
            db.Add(new Widget { Id = Guid.NewGuid(), Name = $"w{i}" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await CreateParser(new() { ["limit"] = "2", ["page"] = "1" })
            .ApplyWithProjectionAsync(db.Widgets.OrderBy(w => w.Name));

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.Limit);
        Assert.Equal(5, first.TotalItems);
        Assert.Equal(3, first.TotalPages);
        Assert.True(first.HasMore);
        Assert.Null(first.NextCursor);
        Assert.Null(first.PreviousCursor);

        var middle = await CreateParser(new() { ["limit"] = "2", ["page"] = "2" })
            .ApplyWithProjectionAsync(db.Widgets.OrderBy(w => w.Name));
        Assert.True(middle.HasMore);

        var last = await CreateParser(new() { ["limit"] = "2", ["page"] = "3" })
            .ApplyWithProjectionAsync(db.Widgets.OrderBy(w => w.Name));
        Assert.Single(last.Items);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task HasMore_is_false_when_everything_fits_on_one_page()
    {
        await using var db = CreateDb();
        db.AddRange(
            new Widget { Id = Guid.NewGuid(), Name = "a" },
            new Widget { Id = Guid.NewGuid(), Name = "b" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var page = await CreateParser().ApplyWithProjectionAsync(db.Widgets.OrderBy(w => w.Name));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(VQueryParser.DefaultLimit, page.Limit);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task HasMore_is_false_for_an_empty_result()
    {
        await using var db = CreateDb();

        var page = await CreateParser().ApplyWithProjectionAsync(db.Widgets);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);
        Assert.Equal(0, page.TotalPages);
        Assert.False(page.HasMore);
    }

    // ── Helpers (the fixtures live with the null-navigation tests) ──

    private static VQueryParser CreateParser(Dictionary<string, string>? queryParams = null)
    {
        var query = new QueryCollection(
            queryParams?.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value))
            ?? []);

        return new VQueryParser(query, new WidgetQueryFilter());
    }

    private static WidgetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WidgetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
