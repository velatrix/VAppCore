using System.Linq.Expressions;

namespace VAppCore;

/// <summary>
/// Non-generic interface for runtime access to filter configuration.
/// </summary>
public interface IVQueryFilter
{
    Type EntityType { get; }
    IReadOnlySet<string> FilterableFields { get; }
    IReadOnlySet<string> SortableFields { get; }
    IReadOnlySet<string> SelectableFields { get; }
    IReadOnlyList<string>? DefaultSelect { get; }
    string? DefaultSort { get; }
    IReadOnlyDictionary<string, CustomFieldDefinition> CustomFields { get; }
    string ResolveFieldName(string fieldName);
    bool IsFieldFilterable(string fieldName);
    bool IsFieldSortable(string fieldName);
    bool IsFieldSelectable(string fieldName);
    void ValidateFilterFields(IEnumerable<string> fields);
    void ValidateSortFields(IEnumerable<string> fields);
    void ValidateSelectFields(IEnumerable<string> fields);
}

/// <summary>
/// Stores the definition of a custom computed field.
/// </summary>
public class CustomFieldDefinition
{
    public string Name { get; set; } = string.Empty;
    public string? Expression { get; set; }
    public string? NavigationProperty { get; set; }
    public List<(string Property, string Alias)> SubFields { get; set; } = [];
    public string? CollectionProperty { get; set; }
    public CustomFieldType FieldType { get; set; }
    public bool IsSelectable { get; set; }
    public bool IsSortable { get; set; }
    public bool IsFilterable { get; set; }

    /// <summary>
    /// Property path used for null-check filtering (e.g. "Flow" for isAutomated).
    /// When set, boolean filter values are translated to null/not-null checks on this property.
    /// </summary>
    public string? NullCheckProperty { get; set; }

    /// <summary>
    /// Builds the dynamic LINQ expression string for this custom field.
    /// </summary>
    public string BuildExpression()
    {
        return FieldType switch
        {
            CustomFieldType.Navigation => BuildNavigationExpression(),
            CustomFieldType.Count => $"it.{CollectionProperty}.Count()",
            CustomFieldType.Raw => Expression ?? "null",
            _ => "null"
        };
    }

    /// <summary>
    /// Gets the navigation properties that need to be included for this field.
    /// </summary>
    public IEnumerable<string> GetRequiredIncludes()
    {
        if (FieldType == CustomFieldType.Navigation && NavigationProperty != null)
            yield return NavigationProperty;
    }

    private string BuildNavigationExpression()
    {
        var subFieldProjections = string.Join(", ",
            SubFields.Select(sf => $"it.{NavigationProperty}.{sf.Property} as {sf.Alias}"));
        return $"iif(it.{NavigationProperty} != null, new ({subFieldProjections}), null)";
    }
}

public enum CustomFieldType
{
    Navigation,
    Count,
    Raw
}

/// <summary>
/// Base class for defining type-safe query filters with whitelisted fields.
/// </summary>
/// <typeparam name="T">The entity type to filter</typeparam>
public abstract class VQueryFilter<T> : IVQueryFilter where T : class
{
    private readonly HashSet<string> _filterableFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sortableFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectableFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fieldAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CustomFieldDefinition> _customFields = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultSort;
    private List<string>? _defaultSelect;

    public Type EntityType => typeof(T);
    public IReadOnlySet<string> FilterableFields => _filterableFields;
    public IReadOnlySet<string> SortableFields => _sortableFields;
    public IReadOnlySet<string> SelectableFields => _selectableFields;
    public IReadOnlyDictionary<string, string> FieldAliases => _fieldAliases;
    public IReadOnlyDictionary<string, CustomFieldDefinition> CustomFields => _customFields;
    public string? DefaultSort => _defaultSort;
    public IReadOnlyList<string>? DefaultSelect => _defaultSelect;

    /// <summary>
    /// Sets the default sort to use when no sort parameter is provided.
    /// Format: "+field" for ascending, "-field" for descending. Can include multiple fields: "-createdAt,+id"
    /// </summary>
    protected void SetDefaultSort(string sort)
    {
        _defaultSort = sort;
    }

    /// <summary>
    /// Sets the default fields to select when no select parameter is provided.
    /// If not set, all selectable fields are returned.
    /// </summary>
    protected void SetDefaultSelect(params string[] fields)
    {
        _defaultSelect = fields.ToList();
    }

    /// <summary>
    /// Registers a field for configuration. Chain with .Filterable(), .Sortable(), .Selectable(), etc.
    /// </summary>
    protected FieldConfig Field<TProperty>(Expression<Func<T, TProperty>> propertyExpression)
    {
        var propertyName = GetPropertyPath(propertyExpression);
        return new FieldConfig(this, propertyName);
    }

    /// <summary>
    /// Registers a collection element's field for configuration.
    /// Use this for nested properties within collections.
    /// Example: CollectionField(x => x.Suites, s => s.Name).Selectable()
    /// </summary>
    /// <typeparam name="TElement">The element type of the collection (inferred automatically)</typeparam>
    protected FieldConfig CollectionField<TElement>(
        Expression<Func<T, IEnumerable<TElement>>> collectionExpression,
        Expression<Func<TElement, object?>> propertyExpression)
    {
        var collectionName = GetPropertyPath(collectionExpression);
        var propertyName = GetSimplePropertyPath(propertyExpression);
        return new FieldConfig(this, $"{collectionName}.{propertyName}");
    }

    /// <summary>
    /// Allows all public properties of the entity for filtering, sorting, and selection.
    /// </summary>
    protected void AllowAll()
    {
        foreach (var prop in typeof(T).GetProperties())
        {
            _filterableFields.Add(prop.Name);
            _sortableFields.Add(prop.Name);
            _selectableFields.Add(prop.Name);
        }
    }

    internal void AddFilterable(string fieldName)
    {
        _filterableFields.Add(fieldName);
    }

    internal void AddSortable(string fieldName)
    {
        _sortableFields.Add(fieldName);
    }

    internal void AddSelectable(string fieldName)
    {
        _selectableFields.Add(fieldName);
    }

    internal void AddAlias(string alias, string fieldName)
    {
        _fieldAliases[alias] = fieldName;
    }

    internal void AddCustomField(CustomFieldDefinition definition)
    {
        _customFields[definition.Name] = definition;
        if (definition.IsSelectable)
            _selectableFields.Add(definition.Name);
        if (definition.IsSortable)
            _sortableFields.Add(definition.Name);
        if (definition.IsFilterable)
            _filterableFields.Add(definition.Name);
    }

    /// <summary>
    /// Resolves a field name, following aliases if defined.
    /// </summary>
    public string ResolveFieldName(string fieldName)
    {
        return _fieldAliases.TryGetValue(fieldName, out var resolved) ? resolved : fieldName;
    }

    /// <summary>
    /// Checks if a field is allowed for filtering.
    /// Accepts both the actual field name and any alias.
    /// </summary>
    public bool IsFieldFilterable(string fieldName)
    {
        // Check direct field name
        if (_filterableFields.Contains(fieldName))
            return true;
        // Check if it's an alias of a filterable field
        if (_fieldAliases.TryGetValue(fieldName, out var resolved))
            return _filterableFields.Contains(resolved);
        return false;
    }

    /// <summary>
    /// Checks if a field is allowed for sorting.
    /// Accepts both the actual field name and any alias.
    /// </summary>
    public bool IsFieldSortable(string fieldName)
    {
        // Check direct field name
        if (_sortableFields.Contains(fieldName))
            return true;
        // Check if it's an alias of a sortable field
        if (_fieldAliases.TryGetValue(fieldName, out var resolved))
            return _sortableFields.Contains(resolved);
        return false;
    }

    /// <summary>
    /// Checks if a field is allowed for selection.
    /// Accepts both the actual field name and any alias.
    /// </summary>
    public bool IsFieldSelectable(string fieldName)
    {
        // Check direct field name
        if (_selectableFields.Contains(fieldName))
            return true;
        // Check if it's an alias of a selectable field
        if (_fieldAliases.TryGetValue(fieldName, out var resolved))
            return _selectableFields.Contains(resolved);
        return false;
    }

    /// <summary>
    /// Validates that all fields in the given list are allowed for filtering.
    /// For nested fields like "user.email", validates the full path.
    /// </summary>
    public void ValidateFilterFields(IEnumerable<string> fields)
    {
        var disallowed = fields.Where(f => !IsFieldFilterable(f)).ToList();
        if (disallowed.Count > 0)
        {
            throw new RsqlValidationException(
                $"Field(s) not allowed for filtering: {string.Join(", ", disallowed)}. " +
                $"Allowed fields: {string.Join(", ", _filterableFields)}");
        }
    }

    /// <summary>
    /// Validates that all sort fields are allowed.
    /// For nested fields like "user.email", validates the full path.
    /// </summary>
    public void ValidateSortFields(IEnumerable<string> fields)
    {
        var disallowed = fields.Where(f => !IsFieldSortable(f)).ToList();
        if (disallowed.Count > 0)
        {
            throw new RsqlValidationException(
                $"Field(s) not allowed for sorting: {string.Join(", ", disallowed)}. " +
                $"Allowed sort fields: {string.Join(", ", _sortableFields)}");
        }
    }

    /// <summary>
    /// Validates that all select fields are allowed.
    /// For nested fields like "user.email", validates the full path.
    /// </summary>
    public void ValidateSelectFields(IEnumerable<string> fields)
    {
        var disallowed = fields.Where(f => !IsFieldSelectable(f)).ToList();
        if (disallowed.Count > 0)
        {
            throw new RsqlValidationException(
                $"Field(s) not allowed for selection: {string.Join(", ", disallowed)}. " +
                $"Allowed select fields: {string.Join(", ", _selectableFields)}");
        }
    }

    private static string GetPropertyPath<TProperty>(Expression<Func<T, TProperty>> expression)
    {
        var parts = new List<string>();
        Expression? current = expression.Body;

        // Handle Convert expressions (for value types boxed to object)
        if (current is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            current = unary.Operand;

        while (current != null)
        {
            if (current is MemberExpression member)
            {
                parts.Insert(0, member.Member.Name);
                current = member.Expression;
            }
            else if (current is MethodCallExpression methodCall)
            {
                // Handle methods like .First(), .FirstOrDefault() on collections
                // Skip the method and continue with the source collection
                if (methodCall.Arguments.Count > 0)
                {
                    // Extension method: First argument is the source (e.g., Enumerable.First(source))
                    current = methodCall.Arguments[0];
                }
                else if (methodCall.Object != null)
                {
                    // Instance method
                    current = methodCall.Object;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        if (parts.Count == 0)
            throw new ArgumentException("Expression must be a property access expression", nameof(expression));

        return string.Join(".", parts);
    }

    private static string GetSimplePropertyPath<TSource>(Expression<Func<TSource, object?>> expression)
    {
        var parts = new List<string>();
        Expression? current = expression.Body;

        // Handle Convert expressions (for value types boxed to object)
        if (current is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            current = unary.Operand;

        while (current is MemberExpression member)
        {
            parts.Insert(0, member.Member.Name);
            current = member.Expression;
        }

        if (parts.Count == 0)
            throw new ArgumentException("Expression must be a property access expression", nameof(expression));

        return string.Join(".", parts);
    }

    /// <summary>
    /// Registers a custom computed field (nullable navigation, collection count, or raw expression).
    /// </summary>
    protected CustomFieldConfig CustomField(string name)
    {
        return new CustomFieldConfig(this, name);
    }

    /// <summary>
    /// Fluent configuration for a custom computed field.
    /// </summary>
    public class CustomFieldConfig
    {
        private readonly VQueryFilter<T> _filter;
        private readonly CustomFieldDefinition _definition;

        internal CustomFieldConfig(VQueryFilter<T> filter, string name)
        {
            _filter = filter;
            _definition = new CustomFieldDefinition { Name = name };
        }

        /// <summary>
        /// Configures this custom field as a nullable navigation property projection.
        /// Returns the nested object when the navigation exists, null otherwise.
        /// </summary>
        public CustomFieldConfig FromNavigation(string navigationProperty)
        {
            _definition.FieldType = CustomFieldType.Navigation;
            _definition.NavigationProperty = navigationProperty;
            return this;
        }

        /// <summary>
        /// Adds a sub-field to project from a navigation property.
        /// </summary>
        public CustomFieldConfig SubField(string property, string alias)
        {
            _definition.SubFields.Add((property, alias));
            return this;
        }

        /// <summary>
        /// Configures this custom field as a collection count.
        /// </summary>
        public CustomFieldConfig CountOf(string collectionProperty)
        {
            _definition.FieldType = CustomFieldType.Count;
            _definition.CollectionProperty = collectionProperty;
            return this;
        }

        /// <summary>
        /// Configures this custom field with a raw dynamic LINQ expression.
        /// </summary>
        public CustomFieldConfig WithExpression(string expression)
        {
            _definition.FieldType = CustomFieldType.Raw;
            _definition.Expression = expression;
            return this;
        }

        /// <summary>
        /// Configures this custom field as a boolean null-check on a navigation property.
        /// Filtering with ==true checks navigation != null, ==false checks navigation == null.
        /// Projection returns true/false.
        /// </summary>
        public CustomFieldConfig WithNullCheck(string navigationProperty)
        {
            _definition.NullCheckProperty = navigationProperty;
            _definition.FieldType = CustomFieldType.Raw;
            _definition.Expression = $"it.{navigationProperty} != null";
            return this;
        }

        public CustomFieldConfig Selectable()
        {
            _definition.IsSelectable = true;
            _filter.AddCustomField(_definition);
            return this;
        }

        public CustomFieldConfig Sortable()
        {
            _definition.IsSortable = true;
            _filter.AddCustomField(_definition);
            return this;
        }

        public CustomFieldConfig Filterable()
        {
            _definition.IsFilterable = true;
            _filter.AddCustomField(_definition);
            return this;
        }
    }

    /// <summary>
    /// Fluent configuration for a field.
    /// </summary>
    public class FieldConfig
    {
        private readonly VQueryFilter<T> _filter;
        private readonly string _fieldName;

        internal FieldConfig(VQueryFilter<T> filter, string fieldName)
        {
            _filter = filter;
            _fieldName = fieldName;
        }

        /// <summary>
        /// Allows this field to be used in RSQL filter queries.
        /// </summary>
        public FieldConfig Filterable()
        {
            _filter.AddFilterable(_fieldName);
            return this;
        }

        /// <summary>
        /// Allows this field to be used for sorting.
        /// </summary>
        public FieldConfig Sortable()
        {
            _filter.AddSortable(_fieldName);
            return this;
        }

        /// <summary>
        /// Allows this field to be selected in the response.
        /// </summary>
        public FieldConfig Selectable()
        {
            _filter.AddSelectable(_fieldName);
            return this;
        }

        /// <summary>
        /// Adds an alias for this field (e.g., "user_name" -> "Username").
        /// The alias inherits the same capabilities (filterable/sortable/selectable) as the original field.
        /// </summary>
        public FieldConfig WithAlias(string alias)
        {
            _filter.AddAlias(alias, _fieldName);
            return this;
        }
    }
}

/// <summary>
/// Exception thrown when RSQL validation fails.
/// </summary>
public class RsqlValidationException : Exception
{
    public RsqlValidationException(string message) : base(message) { }
}