using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace VAppCore.Tests;

/// <summary>
/// Regression cover for projecting declared nested fields across an <b>optional</b> navigation.
///
/// A field such as <c>Field(w =&gt; w.Crate!.CatalogId)</c> reaches a non-nullable value type through a
/// navigation that may be absent. The projection builder used to emit the bare path, so a row without
/// the navigation put a NULL into a non-nullable property: relational providers fail it with
/// "Nullable object must have a value", the in-memory provider with a null dereference. Either way the
/// whole page fails because of rows that are merely missing an optional relation.
///
/// The builder now wraps nested leaves in dynamic LINQ's <c>np()</c>, which null-propagates the entire
/// access chain and lifts a value type to <see cref="Nullable{T}"/>.
/// </summary>
public class VQueryParserNullNavigationTests
{
    [Fact]
    public async Task Projects_null_for_a_missing_optional_navigation_instead_of_throwing()
    {
        await using var db = CreateDb();
        var withCrate = new Widget { Id = Guid.NewGuid(), Name = "packed", Crate = NewCrate("A-1", 7) };
        var without = new Widget { Id = Guid.NewGuid(), Name = "loose" };
        db.AddRange(withCrate, without);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var page = await CreateParser().ApplyWithProjectionAsync(db.Widgets.OrderBy(w => w.Name));

        Assert.Equal(2, page.TotalItems);

        // The row that has the relation still reports every field.
        dynamic packed = page.Items[1];
        Assert.Equal("packed", packed.name);
        Assert.Equal(withCrate.Crate!.CatalogId, (Guid?)packed.crate.catalogId);
        Assert.Equal(7, (int?)packed.crate.slot);
        Assert.Equal("A-1", (string?)packed.crate.label);

        // The row that does not is projected as nulls rather than failing the page.
        dynamic loose = page.Items[0];
        Assert.Equal("loose", loose.name);
        Assert.Null((Guid?)loose.crate.catalogId);
        Assert.Null((int?)loose.crate.slot);
        Assert.Null((string?)loose.crate.label);
    }

    [Fact]
    public async Task Null_propagates_through_every_hop_of_a_deep_chain()
    {
        await using var db = CreateDb();
        var full = NewCrate("B-2", 3);
        full.Shelf = new Shelf { Id = Guid.NewGuid(), Level = 4 };

        db.AddRange(
            new Widget { Id = Guid.NewGuid(), Name = "a-deep", Crate = full },
            new Widget { Id = Guid.NewGuid(), Name = "b-midway", Crate = NewCrate("C-3", 9) }, // crate, no shelf
            new Widget { Id = Guid.NewGuid(), Name = "c-absent" });                            // no crate at all
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var page = await CreateParser().ApplyWithProjectionAsync(db.Widgets.OrderBy(w => w.Name));

        Assert.Equal(4, (int?)((dynamic)page.Items[0]).crate.shelf.level);
        Assert.Null((int?)((dynamic)page.Items[1]).crate.shelf.level); // null at the last hop
        Assert.Null((int?)((dynamic)page.Items[2]).crate.shelf.level); // null at the first hop
    }

    [Fact]
    public async Task An_explicitly_selected_nested_field_behaves_the_same()
    {
        await using var db = CreateDb();
        db.Add(new Widget { Id = Guid.NewGuid(), Name = "loose" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var parser = CreateParser(new() { ["select"] = "catalog_id" });
        var page = await parser.ApplyWithProjectionAsync(db.Widgets);

        Assert.Null((Guid?)((dynamic)page.Items[0]).crate.catalogId);
    }

    // ── Helpers ──

    private static Crate NewCrate(string label, int slot) =>
        new() { Id = Guid.NewGuid(), CatalogId = Guid.NewGuid(), Label = label, Slot = slot };

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

// ── Fixtures ──

public class Widget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? CrateId { get; set; }
    public Crate? Crate { get; set; }
}

public class Crate
{
    public Guid Id { get; set; }
    public Guid CatalogId { get; set; }
    public int Slot { get; set; }
    public string Label { get; set; } = string.Empty;
    public Guid? ShelfId { get; set; }
    public Shelf? Shelf { get; set; }
}

public class Shelf
{
    public Guid Id { get; set; }
    public int Level { get; set; }
}

public class WidgetQueryFilter : VQueryFilter<Widget>
{
    public WidgetQueryFilter()
    {
        Field(w => w.Id).Selectable();
        Field(w => w.Name).Sortable().Selectable();
        // The shapes that mattered: a non-nullable value type one hop away, and two hops away.
        Field(w => w.Crate!.CatalogId).Selectable().WithAlias("catalog_id");
        Field(w => w.Crate!.Slot).Selectable().WithAlias("slot");
        Field(w => w.Crate!.Label).Selectable().WithAlias("label");
        Field(w => w.Crate!.Shelf!.Level).Selectable().WithAlias("shelf_level");
    }
}

internal class WidgetDbContext(DbContextOptions<WidgetDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}
