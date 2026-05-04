using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

public class CursorPaginationTests
{
    private static readonly CursorCodec Codec = new(new NoOpCursorProtector());

    private class ProductFilter : VQueryFilter<TestProduct>
    {
        public ProductFilter()
        {
            Field(x => x.Id).Filterable().Sortable().Selectable();
            Field(x => x.Name).Filterable().Sortable().Selectable();
            Field(x => x.Price).Filterable().Sortable().Selectable();
            SetDefaultSort("+name");
        }
    }

    private class ProductFilterWithPageNav : VQueryFilter<TestProduct>
    {
        public ProductFilterWithPageNav()
        {
            Field(x => x.Id).Filterable().Sortable().Selectable();
            Field(x => x.Name).Filterable().Sortable().Selectable();
            Field(x => x.Price).Filterable().Sortable().Selectable();
            SetDefaultSort("+name");
            EnablePageNavigation();
        }
    }

    private static VQueryParser MakeParser(params (string Key, string Val)[] qp)
        => MakeParserWith(null, qp);

    private static VQueryParser MakeParserWith(IVQueryFilter? filter, params (string Key, string Val)[] qp)
    {
        var dict = qp.ToDictionary(t => t.Key, t => new StringValues(t.Val));
        return new VQueryParser(new QueryCollection(dict), filter ?? new ProductFilter());
    }

    private static async Task<VanillaDbContext> SeedAsync(int count)
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        for (int i = 1; i <= count; i++)
            db.Products.Add(new TestProduct { Id = Guid.NewGuid(), Name = $"Product{i:D2}", Price = i * 10m });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return db;
    }

    // ── Forward cursor ──

    [Fact]
    public async Task Forward_FirstPage_ReturnsFirstLimitItems_WithNextCursor()
    {
        var db = await SeedAsync(10);
        var parser = MakeParser(("limit", "3"));

        var res = await parser.ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal(3, res.Items.Count);
        Assert.Equal("Product01", res.Items[0].Name);
        Assert.Equal("Product03", res.Items[2].Name);
        Assert.NotNull(res.NextCursor);
        Assert.Null(res.PreviousCursor); // first page
        Assert.True(res.HasMore);
    }

    [Fact]
    public async Task Forward_SecondPage_ContinuesFromCursor()
    {
        var db = await SeedAsync(10);
        var first = await MakeParser(("limit", "3"))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        var second = await MakeParser(("limit", "3"), ("cursor", first.NextCursor!))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal(3, second.Items.Count);
        Assert.Equal("Product04", second.Items[0].Name);
        Assert.Equal("Product06", second.Items[2].Name);
        Assert.NotNull(second.PreviousCursor); // not first page anymore
    }

    [Fact]
    public async Task Forward_LastPage_HasMoreFalse_NoNextCursor()
    {
        var db = await SeedAsync(10);
        // Walk to the last page (Product09, Product10 — 2 items if size 3)
        var p1 = await MakeParser(("limit", "3")).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        var p2 = await MakeParser(("limit", "3"), ("cursor", p1.NextCursor!)).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        var p3 = await MakeParser(("limit", "3"), ("cursor", p2.NextCursor!)).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        var p4 = await MakeParser(("limit", "3"), ("cursor", p3.NextCursor!)).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal("Product10", p4.Items[0].Name);
        Assert.False(p4.HasMore);
        Assert.Null(p4.NextCursor);
    }

    [Fact]
    public async Task Forward_LimitChange_BetweenRequests_Works()
    {
        var db = await SeedAsync(10);
        var first = await MakeParser(("limit", "5"))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        // Change limit from 5 to 2
        var second = await MakeParser(("limit", "2"), ("cursor", first.NextCursor!))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal(2, second.Items.Count);
        Assert.Equal("Product06", second.Items[0].Name);
        Assert.Equal("Product07", second.Items[1].Name);
    }

    // ── Sort change discards cursor ──

    [Fact]
    public async Task SortChange_DiscardsCursor_ReturnsPage1OfNewSort()
    {
        var db = await SeedAsync(10);
        // First page with default sort (+name)
        var first = await MakeParser(("limit", "3"))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        // Reuse cursor with different sort (-price)
        var second = await MakeParser(("limit", "3"), ("cursor", first.NextCursor!), ("sort", "-price"))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        // Expectation: cursor was discarded → page 1 of -price sort → most expensive first
        Assert.Equal(3, second.Items.Count);
        Assert.Equal("Product10", second.Items[0].Name); // highest price
        Assert.Null(second.PreviousCursor); // signal: cursor was reset
    }

    // ── Backward cursor (?before=X) ──

    [Fact]
    public async Task Backward_FromMiddleCursor_ReturnsPriorItemsInOrder()
    {
        var db = await SeedAsync(10);
        // Get to the middle: page 1 (items 1-3), page 2 (items 4-6), page 3 (items 7-9)
        var p1 = await MakeParser(("limit", "3")).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        var p2 = await MakeParser(("limit", "3"), ("cursor", p1.NextCursor!)).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        var p3 = await MakeParser(("limit", "3"), ("cursor", p2.NextCursor!)).ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        // Now go BACK using p3.PreviousCursor — should give us p2's items
        var back = await MakeParser(("limit", "3"), ("before", p3.PreviousCursor!))
            .ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal(3, back.Items.Count);
        Assert.Equal("Product04", back.Items[0].Name);
        Assert.Equal("Product06", back.Items[2].Name);
    }

    // ── Cursor + page conflict ──

    [Fact]
    public async Task Cursor_AndPage_BothSupplied_Throws()
    {
        var db = await SeedAsync(5);
        var parser = MakeParserWith(new ProductFilterWithPageNav(),
            ("cursor", "some-cursor"), ("page", "2"));

        await Assert.ThrowsAsync<RsqlValidationException>(
            () => parser.ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken));
    }

    // ── Empty cursor treated as no cursor ──

    [Fact]
    public async Task EmptyCursor_TreatedAsFirstPage()
    {
        var db = await SeedAsync(5);
        var parser = MakeParser(("limit", "3"), ("cursor", ""));

        var res = await parser.ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal(3, res.Items.Count);
        Assert.Equal("Product01", res.Items[0].Name);
        Assert.Null(res.PreviousCursor);
    }

    // ── Malformed cursor ──

    [Fact]
    public async Task MalformedCursor_Throws()
    {
        var db = await SeedAsync(5);
        var parser = MakeParser(("cursor", "garbage!@#"));

        await Assert.ThrowsAsync<CursorDecodeException>(
            () => parser.ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken));
    }

    // ── Page mode requires EnablePageNavigation ──

    [Fact]
    public async Task PageMode_OnFilterWithoutOptIn_ThrowsViaService()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        for (int i = 1; i <= 5; i++)
            db.Products.Add(new TestProduct { Id = Guid.NewGuid(), Name = $"P{i}", Price = i });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new TestPageService { Db = db, CursorCodec = Codec };
        var parser = MakeParserWith(new ProductFilter(), ("page", "2"));

        await Assert.ThrowsAsync<RsqlValidationException>(
            () => svc.GetPaged(parser));
    }

    [Fact]
    public async Task PageMode_OnFilterWithOptIn_Works()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        for (int i = 1; i <= 10; i++)
            db.Products.Add(new TestProduct { Id = Guid.NewGuid(), Name = $"P{i:D2}", Price = i });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new TestPageService { Db = db, CursorCodec = Codec };
        var parser = MakeParserWith(new ProductFilterWithPageNav(), ("page", "2"), ("limit", "3"));

        var res = await svc.GetPaged(parser);

        Assert.Equal(2, res.Page);
        Assert.Equal(10, res.TotalItems);
    }

    private class TestPageService : VService<TestProduct, Guid, Guid, Guid>
    {
        public Task<VPagedResponse<object>> GetPaged(VQueryParser parser) => GetPagedAsync(parser);
    }

    // ── CustomField sort rejected in cursor mode ──

    private class FilterWithCustomFieldSort : VQueryFilter<TestProduct>
    {
        public FilterWithCustomFieldSort()
        {
            Field(x => x.Id).Filterable().Sortable().Selectable();
            Field(x => x.Name).Filterable().Sortable().Selectable();
            CustomField("nameLength")
                .WithExpression("it.Name.Length")
                .Sortable()
                .Selectable();
            SetDefaultSort("+name");
        }
    }

    [Fact]
    public async Task CustomFieldSort_InCursorMode_Throws400()
    {
        var db = await SeedAsync(5);
        var parser = MakeParserWith(new FilterWithCustomFieldSort(), ("sort", "+nameLength"), ("limit", "3"));

        var ex = await Assert.ThrowsAsync<RsqlValidationException>(
            () => parser.ApplyWithCursorAsync(db.Products.AsQueryable(), Codec, TestContext.Current.CancellationToken));

        Assert.Contains("nameLength", ex.Message);
        Assert.Contains("custom", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
