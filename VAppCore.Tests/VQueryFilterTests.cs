namespace VAppCore.Tests;

public class VQueryFilterTests
{
    // ── Field registration ──

    [Fact]
    public void FilterableFields_ContainsConfiguredFields()
    {
        var filter = new UserQueryFilter();

        Assert.Contains("Id", filter.FilterableFields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Name", filter.FilterableFields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Age", filter.FilterableFields, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SortableFields_ContainsConfiguredFields()
    {
        var filter = new UserQueryFilter();

        Assert.Contains("Id", filter.SortableFields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Name", filter.SortableFields, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Email", filter.SortableFields, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectableFields_ContainsConfiguredFields()
    {
        var filter = new UserQueryFilter();

        Assert.Contains("Id", filter.SelectableFields, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Email", filter.SelectableFields, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Age", filter.SelectableFields, StringComparer.OrdinalIgnoreCase);
    }

    // ── Aliases ──

    [Fact]
    public void ResolveFieldName_ResolvesAlias()
    {
        var filter = new UserQueryFilter();

        Assert.Equal("Name", filter.ResolveFieldName("username"));
    }

    [Fact]
    public void ResolveFieldName_NoAlias_ReturnsSameName()
    {
        var filter = new UserQueryFilter();

        Assert.Equal("Id", filter.ResolveFieldName("Id"));
    }

    [Fact]
    public void IsFieldFilterable_AcceptsAlias()
    {
        var filter = new UserQueryFilter();

        Assert.True(filter.IsFieldFilterable("username"));
    }

    [Fact]
    public void IsFieldSortable_AcceptsAlias()
    {
        var filter = new UserQueryFilter();

        Assert.True(filter.IsFieldSortable("username"));
    }

    [Fact]
    public void IsFieldSelectable_AcceptsAlias()
    {
        var filter = new UserQueryFilter();

        Assert.True(filter.IsFieldSelectable("username"));
    }

    // ── Validation ──

    [Fact]
    public void ValidateFilterFields_AllowedFields_DoesNotThrow()
    {
        var filter = new UserQueryFilter();

        filter.ValidateFilterFields(["Id", "Name", "Email"]);
    }

    [Fact]
    public void ValidateFilterFields_DisallowedField_Throws()
    {
        var filter = new UserQueryFilter();

        var ex = Assert.Throws<RsqlValidationException>(() =>
            filter.ValidateFilterFields(["Id", "NonExistent"]));

        Assert.Contains("NonExistent", ex.Message);
        Assert.Contains("not allowed for filtering", ex.Message);
    }

    [Fact]
    public void ValidateSortFields_DisallowedField_Throws()
    {
        var filter = new UserQueryFilter();

        // Email is filterable but NOT sortable
        var ex = Assert.Throws<RsqlValidationException>(() =>
            filter.ValidateSortFields(["Email"]));

        Assert.Contains("Email", ex.Message);
    }

    [Fact]
    public void ValidateSelectFields_DisallowedField_Throws()
    {
        var filter = new UserQueryFilter();

        // Age is filterable/sortable but NOT selectable
        var ex = Assert.Throws<RsqlValidationException>(() =>
            filter.ValidateSelectFields(["Age"]));

        Assert.Contains("Age", ex.Message);
    }

    // ── Default sort/select ──

    [Fact]
    public void DefaultSort_ReturnsConfiguredValue()
    {
        var filter = new UserQueryFilter();

        Assert.Equal("-createdAt", filter.DefaultSort);
    }

    [Fact]
    public void DefaultSelect_ReturnsConfiguredFields()
    {
        var filter = new UserQueryFilter();

        Assert.NotNull(filter.DefaultSelect);
        Assert.Equal(["id", "name", "email"], filter.DefaultSelect);
    }

    // ── Nested fields ──

    [Fact]
    public void NestedField_IsFilterable()
    {
        var filter = new UserQueryFilter();

        Assert.True(filter.IsFieldFilterable("Address.City"));
    }

    // ── AllowAll ──

    [Fact]
    public void AllowAll_AllPublicProperties_Allowed()
    {
        var filter = new AllowAllFilter();

        Assert.True(filter.IsFieldFilterable("Id"));
        Assert.True(filter.IsFieldSortable("Name"));
        Assert.True(filter.IsFieldSelectable("Email"));
    }

    // ── EntityType ──

    [Fact]
    public void EntityType_ReturnsCorrectType()
    {
        var filter = new UserQueryFilter();

        Assert.Equal(typeof(User), filter.EntityType);
    }

    private class AllowAllFilter : VQueryFilter<User>
    {
        public AllowAllFilter() => AllowAll();
    }
}
