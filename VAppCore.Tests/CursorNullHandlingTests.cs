// xunit v3's Assert.Equal&lt;T[]&gt; constrains T : IEquatable&lt;T&gt;, which string? doesn't
// satisfy under nullable ref types. The values genuinely include nulls — using the
// IEnumerable overload via List sidesteps the constraint cleanly.
#pragma warning disable CS8631

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

/// <summary>
/// Cursor pagination on nullable sort fields.
/// Design: NULLs always sort LAST regardless of ASC/DESC direction.
/// Cursor positioned at non-null V (forward, asc): rows with field &gt; V (still non-null) PLUS all NULL rows.
/// Cursor positioned at non-null V (backward, asc): rows with field &lt; V (still non-null only — NULLs are after).
/// Cursor positioned at NULL value (forward): only same-NULL section, ordered by id.
/// Cursor positioned at NULL value (backward): all non-null rows come before.
/// </summary>
public class CursorNullHandlingTests
{
    private static readonly CursorCodec Codec = new(new NoOpCursorProtector());

    private class NullableFilter : VQueryFilter<TestNullableEntity>
    {
        public NullableFilter()
        {
            Field(x => x.Id).Filterable().Sortable().Selectable();
            Field(x => x.OptionalName).Filterable().Sortable().Selectable();
            Field(x => x.OptionalScore).Filterable().Sortable().Selectable();
            SetDefaultSort("+optionalName");
        }
    }

    private static VQueryParser MakeParser(params (string Key, string Val)[] qp)
    {
        var dict = qp.ToDictionary(t => t.Key, t => new StringValues(t.Val));
        return new VQueryParser(new QueryCollection(dict), new NullableFilter());
    }

    private static async Task<VanillaDbContext> SeedMixedAsync()
    {
        var (db, _) = TestFactory.CreateVanillaDbContext();
        // 6 rows: 3 with names, 3 with NULLs
        db.NullableEntities.Add(new TestNullableEntity { Id = Guid.NewGuid(), OptionalName = "A" });
        db.NullableEntities.Add(new TestNullableEntity { Id = Guid.NewGuid(), OptionalName = "B" });
        db.NullableEntities.Add(new TestNullableEntity { Id = Guid.NewGuid(), OptionalName = "C" });
        db.NullableEntities.Add(new TestNullableEntity { Id = Guid.NewGuid(), OptionalName = null });
        db.NullableEntities.Add(new TestNullableEntity { Id = Guid.NewGuid(), OptionalName = null });
        db.NullableEntities.Add(new TestNullableEntity { Id = Guid.NewGuid(), OptionalName = null });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return db;
    }

    // ── ORDER BY: NULLs always last ──

    [Fact]
    public async Task FullList_NullsAppearLast_ASC()
    {
        var db = await SeedMixedAsync();
        var parser = MakeParser(("limit", "10"), ("sort", "+optionalName"));

        var res = await parser.ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal(6, res.Items.Count);
        Assert.Equal<string?>(["A", "B", "C", null, null, null], res.Items.Select(e => e.OptionalName).ToArray());
    }

    [Fact]
    public async Task FullList_NullsAppearLast_DESC()
    {
        var db = await SeedMixedAsync();
        var parser = MakeParser(("limit", "10"), ("sort", "-optionalName"));

        var res = await parser.ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        Assert.Equal<string?>(["C", "B", "A", null, null, null], res.Items.Select(e => e.OptionalName).ToArray());
    }

    // ── Cursor mid non-null section ──

    [Fact]
    public async Task Forward_FromNonNullCursor_ContinuesNonNullThenIncludesNulls()
    {
        var db = await SeedMixedAsync();
        // Page 1: ["A", "B"]
        var p1 = await MakeParser(("limit", "2"), ("sort", "+optionalName"))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        Assert.Equal<string?>(["A", "B"], p1.Items.Select(e => e.OptionalName).ToArray());

        // Page 2: ["C", null]
        var p2 = await MakeParser(("limit", "2"), ("sort", "+optionalName"), ("cursor", p1.NextCursor!))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        Assert.Equal<string?>(["C", null], p2.Items.Select(e => e.OptionalName).ToArray());

        // Page 3: [null, null]
        var p3 = await MakeParser(("limit", "2"), ("sort", "+optionalName"), ("cursor", p2.NextCursor!))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        Assert.Equal(2, p3.Items.Count);
        Assert.All(p3.Items, e => Assert.Null(e.OptionalName));
        Assert.False(p3.HasMore);
    }

    // ── Cursor at NULL position ──

    [Fact]
    public async Task Forward_FromNullCursor_StaysInNullSection()
    {
        var db = await SeedMixedAsync();
        // Walk to the first NULL row
        var p1 = await MakeParser(("limit", "3"), ("sort", "+optionalName"))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        var p2 = await MakeParser(("limit", "1"), ("sort", "+optionalName"), ("cursor", p1.NextCursor!))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        // p2 has the FIRST null row. Continuing forward from its cursor should give us the remaining null rows.
        Assert.Single(p2.Items);
        Assert.Null(p2.Items[0].OptionalName);

        var p3 = await MakeParser(("limit", "5"), ("sort", "+optionalName"), ("cursor", p2.NextCursor!))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        Assert.Equal(2, p3.Items.Count);
        Assert.All(p3.Items, e => Assert.Null(e.OptionalName));
    }

    // ── Backward direction across NULL boundary ──

    [Fact]
    public async Task Backward_FromNullSection_ReturnsNonNullRows()
    {
        var db = await SeedMixedAsync();
        // Walk forward to a null row
        var p1 = await MakeParser(("limit", "4"), ("sort", "+optionalName"))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);
        // p1 = [A, B, C, null]
        Assert.Equal<string?>(["A", "B", "C", null], p1.Items.Select(e => e.OptionalName).ToArray());

        // Now go BACK from the first null
        var back = await MakeParser(("limit", "3"), ("sort", "+optionalName"), ("before", p1.NextCursor!))
            .ApplyWithCursorAsync(db.NullableEntities.AsQueryable(), Codec, TestContext.Current.CancellationToken);

        // Should return the LAST 3 items before the cursor — which is [A, B, C]
        Assert.Equal<string?>(["A", "B", "C"], back.Items.Select(e => e.OptionalName).ToArray());
    }
}
