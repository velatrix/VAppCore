using Microsoft.AspNetCore.Http;

namespace VAppCore.Tests;

public class VQueryParserTests
{
    private static VQueryParser CreateParser(
        Dictionary<string, string>? queryParams = null,
        IVQueryFilter? filterConfig = null)
    {
        var query = new QueryCollection(
            queryParams?.ToDictionary(
                kvp => kvp.Key,
                kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value))
            ?? []);

        return new VQueryParser(query, filterConfig);
    }

    // ── Query parameter reading ──

    [Fact]
    public void Filter_ReadsFromQueryString()
    {
        var parser = CreateParser(new() { ["filter"] = "name==John" });
        Assert.Equal("name==John", parser.Filter);
    }

    [Fact]
    public void Sort_ReadsFromQueryString()
    {
        var parser = CreateParser(new() { ["sort"] = "-name,+age" });
        Assert.Equal("-name,+age", parser.Sort);
    }

    [Fact]
    public void Select_ReadsFromQueryString()
    {
        var parser = CreateParser(new() { ["select"] = "id,name,email" });
        Assert.Equal("id,name,email", parser.Select);
    }

    [Fact]
    public void Page_DefaultsTo1()
    {
        var parser = CreateParser();
        Assert.Equal(1, parser.Page);
    }

    [Fact]
    public void Page_ReadsFromQueryString()
    {
        var parser = CreateParser(new() { ["page"] = "3" });
        Assert.Equal(3, parser.Page);
    }

    [Fact]
    public void Page_InvalidValue_DefaultsTo1()
    {
        var parser = CreateParser(new() { ["page"] = "abc" });
        Assert.Equal(1, parser.Page);
    }

    [Fact]
    public void Page_NegativeValue_DefaultsTo1()
    {
        var parser = CreateParser(new() { ["page"] = "-1" });
        Assert.Equal(1, parser.Page);
    }

    [Fact]
    public void Size_DefaultsTo20()
    {
        var parser = CreateParser();
        Assert.Equal(20, parser.Size);
    }

    [Fact]
    public void Size_ReadsFromQueryString()
    {
        var parser = CreateParser(new() { ["size"] = "50" });
        Assert.Equal(50, parser.Size);
    }

    [Fact]
    public void Size_CapsAt100()
    {
        var parser = CreateParser(new() { ["size"] = "500" });
        Assert.Equal(100, parser.Size);
    }

    // ── EffectiveSort ──

    [Fact]
    public void EffectiveSort_UsesUserSort_WhenProvided()
    {
        var parser = CreateParser(
            new() { ["sort"] = "+name" },
            new UserQueryFilter());

        Assert.Equal("+name", parser.EffectiveSort);
    }

    [Fact]
    public void EffectiveSort_FallsBackToDefault()
    {
        var parser = CreateParser(filterConfig: new UserQueryFilter());

        Assert.Equal("-createdAt", parser.EffectiveSort);
    }

    // ── SelectedFields ──

    [Fact]
    public void SelectedFields_Null_WhenNoSelectAndNoDefault()
    {
        var parser = CreateParser();
        Assert.Null(parser.SelectedFields);
    }

    [Fact]
    public void SelectedFields_UsesDefault_WhenNoSelectParam()
    {
        var parser = CreateParser(filterConfig: new UserQueryFilter());

        Assert.NotNull(parser.SelectedFields);
        Assert.Equal(["id", "name", "email"], parser.SelectedFields);
    }

    [Fact]
    public void SelectedFields_ParsesFromQueryString()
    {
        var parser = CreateParser(
            new() { ["select"] = "id,name" },
            new UserQueryFilter());

        Assert.Equal(2, parser.SelectedFields!.Count);
    }

    [Fact]
    public void SelectedFields_Validates_ThrowsOnDisallowed()
    {
        var parser = CreateParser(
            new() { ["select"] = "id,Age" },
            new UserQueryFilter());

        // Age is not selectable in UserQueryFilter
        Assert.Throws<RsqlValidationException>(() => parser.SelectedFields);
    }

    [Fact]
    public void IsFieldSelected_TrueForAll_WhenNoSelection()
    {
        var parser = CreateParser();

        Assert.True(parser.IsFieldSelected("anything"));
    }

    // ── GetFilterExpression ──

    [Fact]
    public void GetFilterExpression_ReturnsNull_WhenNoFilter()
    {
        var parser = CreateParser();
        Assert.Null(parser.GetFilterExpression<User>());
    }

    [Fact]
    public void GetFilterExpression_ParsesFilter()
    {
        var parser = CreateParser(new() { ["filter"] = "Name==John" });
        var expr = parser.GetFilterExpression<User>();

        Assert.NotNull(expr);
        var func = expr!.Compile();
        Assert.True(func(new User { Name = "John" }));
    }

    [Fact]
    public void GetFilterExpression_ValidatesFields_WhenFilterConfigProvided()
    {
        var parser = CreateParser(
            new() { ["filter"] = "NonExistent==value" },
            new UserQueryFilter());

        Assert.Throws<RsqlValidationException>(() => parser.GetFilterExpression<User>());
    }

    [Fact]
    public void GetFilterExpression_ResolvesAliases()
    {
        var parser = CreateParser(
            new() { ["filter"] = "username==John" },
            new UserQueryFilter());

        var expr = parser.GetFilterExpression<User>();
        var func = expr!.Compile();

        Assert.True(func(new User { Name = "John" }));
        Assert.False(func(new User { Name = "Jane" }));
    }

    // ── Apply (filter + sort + pagination) ──

    [Fact]
    public void Apply_FiltersSortsPaginates()
    {
        var users = Enumerable.Range(1, 50)
            .Select(i => new User { Id = i, Name = $"User{i:D2}", Age = 20 + i })
            .AsQueryable();

        var parser = CreateParser(new()
        {
            ["filter"] = "Age=gt=30",
            ["sort"] = "+Name",
            ["page"] = "1",
            ["size"] = "5"
        });

        var result = parser.Apply(users).ToList();

        Assert.Equal(5, result.Count);
        Assert.All(result, u => Assert.True(u.Age > 30));
        // Should be sorted by name ascending
        Assert.True(string.Compare(result[0].Name, result[1].Name, StringComparison.Ordinal) < 0);
    }

    [Fact]
    public void ApplySort_DescendingSort_Works()
    {
        var users = new List<User>
        {
            new() { Name = "Alice", Age = 30 },
            new() { Name = "Charlie", Age = 25 },
            new() { Name = "Bob", Age = 35 }
        }.AsQueryable();

        var parser = CreateParser(new() { ["sort"] = "-Name" });
        var result = parser.ApplySort(users).ToList();

        Assert.Equal("Charlie", result[0].Name);
        Assert.Equal("Bob", result[1].Name);
        Assert.Equal("Alice", result[2].Name);
    }

    [Fact]
    public void ApplySort_MultipleFields_Works()
    {
        var users = new List<User>
        {
            new() { Name = "Alice", Age = 30 },
            new() { Name = "Alice", Age = 25 },
            new() { Name = "Bob", Age = 35 }
        }.AsQueryable();

        var parser = CreateParser(new() { ["sort"] = "+Name,-Age" });
        var result = parser.ApplySort(users).ToList();

        Assert.Equal("Alice", result[0].Name);
        Assert.Equal(30, result[0].Age); // Higher age first (descending)
        Assert.Equal("Alice", result[1].Name);
        Assert.Equal(25, result[1].Age);
        Assert.Equal("Bob", result[2].Name);
    }

    [Fact]
    public void ApplySort_ValidatesFields_WhenFilterConfigProvided()
    {
        var users = new List<User>().AsQueryable();
        var parser = CreateParser(
            new() { ["sort"] = "+NonExistent" },
            new UserQueryFilter());

        Assert.Throws<RsqlValidationException>(() => parser.ApplySort(users));
    }

    [Fact]
    public void ApplySort_ResolvesAliases()
    {
        var users = new List<User>
        {
            new() { Name = "Charlie" },
            new() { Name = "Alice" },
            new() { Name = "Bob" }
        }.AsQueryable();

        var parser = CreateParser(
            new() { ["sort"] = "+username" },
            new UserQueryFilter());

        var result = parser.ApplySort(users).ToList();
        Assert.Equal("Alice", result[0].Name);
    }

    // ── Pagination ──

    [Fact]
    public void ApplyPagination_SkipsAndTakes()
    {
        var users = Enumerable.Range(1, 100)
            .Select(i => new User { Id = i })
            .AsQueryable();

        var parser = CreateParser(new() { ["page"] = "3", ["size"] = "10" });
        var result = parser.ApplyPagination(users).ToList();

        Assert.Equal(10, result.Count);
        Assert.Equal(21, result[0].Id); // Page 3, size 10 → skip 20
    }

    // ── GetRequiredIncludes ──

    [Fact]
    public void GetRequiredIncludes_DetectsNestedSelectFields()
    {
        var parser = CreateParser(
            new() { ["select"] = "id,Address.City" },
            new UserQueryFilter());

        var includes = parser.GetRequiredIncludes();
        Assert.Contains("Address", includes);
    }

    [Fact]
    public void GetRequiredIncludes_DetectsNestedFilterFields()
    {
        var parser = CreateParser(
            new() { ["filter"] = "Address.City==London" },
            new UserQueryFilter());

        var includes = parser.GetRequiredIncludes();
        Assert.Contains("Address", includes);
    }
}
