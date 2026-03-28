using System.Collections.Concurrent;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace VAppCore;

public class VQueryParser
{
    private static readonly VRsqlParser RsqlParser = new();
    internal static readonly ConcurrentDictionary<Type, IVQueryFilter> FilterCache = new();

    public IQueryCollection Query { get; }
    public IVQueryFilter? FilterConfig { get; }

    private List<string>? _selectedFields;

    public VQueryParser(IQueryCollection query, IVQueryFilter? filterConfig = null)
    {
        Query = query;
        FilterConfig = filterConfig;
    }

    /// <summary>
    /// Minimal API parameter binding. Automatically reads query parameters and resolves
    /// filter config from endpoint metadata (UseVQueryParserAttribute).
    /// </summary>
    public static ValueTask<VQueryParser?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        IVQueryFilter? filterConfig = null;

        var endpoint = context.GetEndpoint();
        var attribute = endpoint?.Metadata.GetMetadata<UseVQueryParserAttribute>();

        if (attribute?.FilterType != null)
        {
            filterConfig = FilterCache.GetOrAdd(attribute.FilterType, type =>
                (IVQueryFilter)Activator.CreateInstance(type)!);
        }

        var parser = new VQueryParser(context.Request.Query, filterConfig);
        return ValueTask.FromResult<VQueryParser?>(parser);
    }

    public string? Get(string key) => Query.TryGetValue(key, out var v) ? v.ToString() : null;

    /// <summary>
    /// Gets the RSQL filter string from the "filter" query parameter
    /// </summary>
    public string? Filter => Get("filter");

    /// <summary>
    /// Gets the sort string from the "sort" query parameter (e.g., "+name,-createdAt")
    /// </summary>
    public string? Sort => Get("sort");

    /// <summary>
    /// Gets the select string from the "select" query parameter (e.g., "username,email,user.email")
    /// </summary>
    public string? Select => Get("select");

    /// <summary>
    /// Gets the effective sort string (user-provided or default from filter config)
    /// </summary>
    public string? EffectiveSort => Sort ?? FilterConfig?.DefaultSort;

    /// <summary>
    /// Returns true if field selection is active (either explicit select or selectable fields configured)
    /// </summary>
    public bool HasSelection => !string.IsNullOrWhiteSpace(Select) ||
                                (FilterConfig?.SelectableFields.Count > 0);

    /// <summary>
    /// Gets the validated selected fields. Returns null if no selection (return all fields).
    /// Supports nested fields like "user.email", "user.username"
    /// </summary>
    public IReadOnlyList<string>? SelectedFields
    {
        get
        {
            if (_selectedFields != null)
                return _selectedFields;

            if (string.IsNullOrWhiteSpace(Select))
            {
                // Use default select if configured
                if (FilterConfig?.DefaultSelect != null)
                {
                    _selectedFields = FilterConfig.DefaultSelect.ToList();
                    return _selectedFields;
                }
                return null;
            }

            var fields = Select
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToList();

            // Validate if filter config is provided
            if (FilterConfig != null)
            {
                FilterConfig.ValidateSelectFields(fields);
                // Resolve aliases for root fields only
                fields = fields.Select(f =>
                {
                    var parts = f.Split('.');
                    parts[0] = FilterConfig.ResolveFieldName(parts[0]);
                    return string.Join(".", parts);
                }).ToList();
            }

            _selectedFields = fields;
            return _selectedFields;
        }
    }

    /// <summary>
    /// Gets the fields to select - either explicit selection, default selection, or all selectable fields
    /// </summary>
    public List<string> GetEffectiveSelectFields()
    {
        if (SelectedFields != null && SelectedFields.Count > 0)
            return SelectedFields.ToList();

        if (FilterConfig?.SelectableFields.Count > 0)
            return FilterConfig.SelectableFields.ToList();

        return [];
    }

    /// <summary>
    /// Gets the navigation properties that need to be included based on select, filter, and sort fields.
    /// E.g., selecting "user.email" or filtering by "user.name" means "User" needs to be included.
    /// </summary>
    public IReadOnlyList<string> GetRequiredIncludes()
    {
        var allFields = new List<string>();

        // Add select fields
        allFields.AddRange(GetEffectiveSelectFields());

        // Add filter fields
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            var node = RsqlParser.Parse(Filter);
            if (node != null)
            {
                var filterFields = node.GetUsedFields();
                // Resolve aliases
                if (FilterConfig != null)
                {
                    filterFields = filterFields.Select(f => FilterConfig.ResolveFieldName(f)).ToList();
                }
                allFields.AddRange(filterFields);
            }
        }

        // Add sort fields
        var sortString = EffectiveSort;
        if (!string.IsNullOrWhiteSpace(sortString))
        {
            var sortFields = sortString
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().TrimStart('+', '-'))
                .Where(f => !string.IsNullOrEmpty(f));

            // Resolve aliases
            if (FilterConfig != null)
            {
                sortFields = sortFields.Select(f => FilterConfig.ResolveFieldName(f));
            }
            allFields.AddRange(sortFields);
        }

        // Extract navigation properties (fields containing '.')
        var includes = allFields
            .Where(f => f.Contains('.'))
            .Select(f => ToPascalCase(f.Split('.')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Add includes required by custom fields
        if (FilterConfig?.CustomFields != null)
        {
            var effectiveFields = GetEffectiveSelectFields();
            foreach (var (name, customField) in FilterConfig.CustomFields)
            {
                if (effectiveFields.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var include in customField.GetRequiredIncludes())
                    {
                        if (!includes.Contains(include, StringComparer.OrdinalIgnoreCase))
                            includes.Add(include);
                    }
                }
            }
        }

        return includes;
    }

    /// <summary>
    /// Returns true if a specific field is in the selection (or if no selection = all fields)
    /// </summary>
    public bool IsFieldSelected(string fieldName)
    {
        if (SelectedFields == null)
            return true; // No selection means all fields

        return SelectedFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the page number from the "page" query parameter (1-based, defaults to 1)
    /// </summary>
    public int Page => int.TryParse(Get("page"), out var p) && p > 0 ? p : 1;

    /// <summary>
    /// Gets the page size from the "size" query parameter (defaults to 20)
    /// </summary>
    public int Size => int.TryParse(Get("size"), out var s) && s > 0 ? Math.Min(s, 100) : 20;

    /// <summary>
    /// Parses the RSQL filter and returns a LINQ expression.
    /// Validates fields against the filter configuration if provided.
    /// </summary>
    public Expression<Func<T, bool>>? GetFilterExpression<T>()
    {
        if (string.IsNullOrWhiteSpace(Filter))
            return null;

        var node = RsqlParser.Parse(Filter);
        if (node == null)
            return null;

        // Validate fields if filter config is provided
        if (FilterConfig != null)
        {
            var usedFields = node.GetUsedFields().Distinct().ToList();
            FilterConfig.ValidateFilterFields(usedFields);

            // Resolve aliases to actual property paths before building expressions
            ResolveAliases(node, FilterConfig);
        }

        return node.ToLinqExpression<T>();
    }

    private static void ResolveAliases(RsqlNode node, IVQueryFilter filterConfig)
    {
        switch (node)
        {
            case ComparisonNode comparison:
                // Check if this is a custom field with null-check filtering
                if (filterConfig.CustomFields.TryGetValue(comparison.Selector, out var customDef)
                    && customDef.NullCheckProperty != null
                    && comparison.Operator == RsqlOperator.Equal
                    && comparison.Arguments.Count == 1)
                {
                    var boolVal = bool.Parse(comparison.Arguments[0]);
                    comparison.Selector = customDef.NullCheckProperty;
                    comparison.Operator = boolVal ? RsqlOperator.IsNotNull : RsqlOperator.IsNull;
                    comparison.Arguments = ["true"];
                }
                else
                {
                    comparison.Selector = filterConfig.ResolveFieldName(comparison.Selector);
                }
                break;
            case AndNode andNode:
                foreach (var child in andNode.Children)
                    ResolveAliases(child, filterConfig);
                break;
            case OrNode orNode:
                foreach (var child in orNode.Children)
                    ResolveAliases(child, filterConfig);
                break;
        }
    }

    /// <summary>
    /// Applies RSQL filter, sorting, and pagination to a queryable.
    /// Does NOT apply field selection - use ApplyWithSelect for that.
    /// </summary>
    public IQueryable<T> Apply<T>(IQueryable<T> queryable)
    {
        // Apply RSQL filter with validation
        var filterExpr = GetFilterExpression<T>();
        if (filterExpr != null)
            queryable = queryable.Where(filterExpr);

        // Apply sorting with validation
        queryable = ApplySort(queryable);

        // Apply pagination
        queryable = queryable.Skip((Page - 1) * Size).Take(Size);

        return queryable;
    }

    /// <summary>
    /// Applies includes, RSQL filter, sorting, and pagination to a queryable.
    /// Returns both the paginated items and the total count (before pagination).
    /// Automatically includes navigation properties based on select, filter, and sort fields.
    /// </summary>
    public async Task<(List<T> Items, int TotalCount)> ApplyWithCountAsync<T>(IQueryable<T> queryable) where T : class
    {
        // Auto-include navigation properties based on selected/filtered/sorted fields
        foreach (var include in GetRequiredIncludes())
        {
            queryable = queryable.Include(include);
        }

        // Apply RSQL filter with validation
        var filterExpr = GetFilterExpression<T>();
        if (filterExpr != null)
            queryable = queryable.Where(filterExpr);

        // Get total count before pagination
        var totalCount = await queryable.CountAsync();

        // Apply sorting with validation
        queryable = ApplySort(queryable);

        // Apply pagination
        queryable = queryable.Skip((Page - 1) * Size).Take(Size);

        var items = await queryable.ToListAsync();

        return (items, totalCount);
    }

    /// <summary>
    /// Applies includes, RSQL filter, sorting, pagination, AND field selection to a queryable.
    /// Returns a VPagedResponse with projected objects containing only the selected/selectable fields.
    /// If no select param is provided, returns all fields marked as Selectable() in the QueryFilter.
    /// </summary>
    public async Task<VPagedResponse<object>> ApplyWithProjectionAsync<T>(IQueryable<T> queryable) where T : class
    {
        // Auto-include navigation properties based on selected/filtered/sorted fields
        foreach (var include in GetRequiredIncludes())
        {
            queryable = queryable.Include(include);
        }

        // Apply RSQL filter with validation
        var filterExpr = GetFilterExpression<T>();
        if (filterExpr != null)
            queryable = queryable.Where(filterExpr);

        // Get total count before pagination
        var totalCount = await queryable.CountAsync();

        // Apply sorting with validation
        queryable = ApplySort(queryable);

        // Apply pagination
        queryable = queryable.Skip((Page - 1) * Size).Take(Size);

        // Get fields to project
        var fields = GetEffectiveSelectFields();
        if (fields.Count == 0)
        {
            // No selectable fields configured - return empty
            return new VPagedResponse<object>
            {
                Items = [],
                Page = Page,
                Size = Size,
                TotalItems = totalCount
            };
        }

        // Build and apply projection
        var selectExpression = BuildDynamicSelectExpression(fields);
        var projected = queryable.Select(selectExpression);
        var items = await projected.ToDynamicListAsync();

        return new VPagedResponse<object>
        {
            Items = items.Cast<object>().ToList(),
            Page = Page,
            Size = Size,
            TotalItems = totalCount
        };
    }

    /// <summary>
    /// Applies RSQL filter, sorting, pagination, AND field selection at SQL level.
    /// Returns dynamic objects with only the selected fields.
    /// Throws if no selectable fields are configured (whitelist required).
    /// </summary>
    public async Task<List<dynamic>> ApplyWithSelectAsync<T>(IQueryable<T> queryable) where T : class
    {
        // Apply filter, sort, pagination first
        queryable = Apply(queryable);

        // Get fields to select
        var fields = GetEffectiveSelectFields();

        if (fields.Count == 0)
        {
            throw new RsqlValidationException(
                "No selectable fields configured. Configure selectable fields in your QueryFilter using .Selectable()");
        }

        // Build dynamic select expression
        // Group by root to handle nested fields
        var selectExpression = BuildDynamicSelectExpression(fields);

        // Apply dynamic select - this generates SQL with only selected columns
        var projected = queryable.Select(selectExpression);

        return await projected.ToDynamicListAsync();
    }

    /// <summary>
    /// Builds a dynamic select expression string for System.Linq.Dynamic.Core
    /// Handles nested fields like "Parent.Name" by creating nested objects
    /// </summary>
    private string BuildDynamicSelectExpression(List<string> fields)
    {
        // Separate custom fields from regular fields
        var customFieldNames = FilterConfig?.CustomFields?.Keys ?? (IEnumerable<string>)[];
        var regularFields = fields.Where(f => !customFieldNames.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var selectedCustomFields = fields.Where(f => customFieldNames.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

        // Group regular fields by their root
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in regularFields)
        {
            var parts = field.Split('.');
            var root = parts[0];

            if (!grouped.ContainsKey(root))
                grouped[root] = [];

            if (parts.Length == 1)
            {
                // Simple field - mark with empty string to include the whole thing
                if (!grouped[root].Contains(""))
                    grouped[root].Insert(0, "");
            }
            else
            {
                // Nested field - add the rest of the path
                var nestedPath = string.Join(".", parts.Skip(1));
                if (!grouped[root].Contains(nestedPath))
                    grouped[root].Add(nestedPath);
            }
        }

        // Build the select expression
        var selectParts = new List<string>();

        foreach (var group in grouped)
        {
            var root = group.Key;
            var subFields = group.Value;

            // Pascal case for the property access
            var propertyName = ToPascalCase(root);

            if (subFields.Contains(""))
            {
                // Include the whole property
                selectParts.Add($"{propertyName} as {ToCamelCase(root)}");
            }
            else if (subFields.Count > 0)
            {
                // Nested selection - create nested object with proper "it." prefix
                var nestedSelect = BuildNestedSelect("it." + propertyName, subFields);
                selectParts.Add($"{nestedSelect} as {ToCamelCase(root)}");
            }
        }

        // Add custom field expressions
        if (FilterConfig?.CustomFields != null)
        {
            foreach (var customFieldName in selectedCustomFields)
            {
                if (FilterConfig.CustomFields.TryGetValue(customFieldName, out var definition))
                {
                    var expr = definition.BuildExpression();
                    selectParts.Add($"{expr} as {ToCamelCase(definition.Name)}");
                }
            }
        }

        return $"new ({string.Join(", ", selectParts)})";
    }

    private string BuildNestedSelect(string rootProperty, List<string> fields)
    {
        // Group by next level
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field)) continue;

            var parts = field.Split('.');
            var current = parts[0];

            if (!grouped.ContainsKey(current))
                grouped[current] = [];

            if (parts.Length == 1)
            {
                grouped[current].Insert(0, "");
            }
            else
            {
                grouped[current].Add(string.Join(".", parts.Skip(1)));
            }
        }

        var selectParts = new List<string>();

        foreach (var group in grouped)
        {
            var prop = ToPascalCase(group.Key);
            var subFields = group.Value;

            if (subFields.Contains("") || subFields.Count == 0)
            {
                selectParts.Add($"{rootProperty}.{prop} as {ToCamelCase(group.Key)}");
            }
            else
            {
                var nested = BuildNestedSelect($"{rootProperty}.{prop}", subFields);
                selectParts.Add($"{nested} as {ToCamelCase(group.Key)}");
            }
        }

        return $"new ({string.Join(", ", selectParts)})";
    }

    /// <summary>
    /// Applies only the RSQL filter to a queryable with validation
    /// </summary>
    public IQueryable<T> ApplyFilter<T>(IQueryable<T> queryable)
    {
        var filterExpr = GetFilterExpression<T>();
        return filterExpr != null ? queryable.Where(filterExpr) : queryable;
    }

    /// <summary>
    /// Applies only sorting to a queryable (uses EffectiveSort: user-provided or default)
    /// </summary>
    public IQueryable<T> ApplySort<T>(IQueryable<T> queryable)
    {
        var sortString = EffectiveSort;
        if (string.IsNullOrWhiteSpace(sortString))
            return queryable;

        var sortFields = sortString.Split(',', StringSplitOptions.RemoveEmptyEntries);

        // Validate sort fields if filter config is provided
        if (FilterConfig != null)
        {
            var fieldNames = sortFields
                .Select(f => f.Trim().TrimStart('+', '-'))
                .Where(f => !string.IsNullOrEmpty(f))
                .ToList();
            FilterConfig.ValidateSortFields(fieldNames);
        }

        IOrderedQueryable<T>? ordered = null;

        foreach (var field in sortFields)
        {
            var trimmed = field.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var descending = trimmed.StartsWith('-');
            var propertyName = trimmed.TrimStart('+', '-');

            // Resolve alias if filter config is provided
            if (FilterConfig != null)
                propertyName = FilterConfig.ResolveFieldName(propertyName);

            var parameter = Expression.Parameter(typeof(T), "x");
            var property = GetPropertyExpression(typeof(T), parameter, propertyName);
            var lambda = Expression.Lambda(property, parameter);

            var methodName = ordered == null
                ? (descending ? "OrderByDescending" : "OrderBy")
                : (descending ? "ThenByDescending" : "ThenBy");

            var method = typeof(Queryable).GetMethods()
                .First(m => m.Name == methodName && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(T), property.Type);

            ordered = (IOrderedQueryable<T>)method.Invoke(null, [ordered ?? queryable, lambda])!;
        }

        return ordered ?? queryable;
    }

    /// <summary>
    /// Applies only pagination (Skip/Take) to a queryable
    /// </summary>
    public IQueryable<T> ApplyPagination<T>(IQueryable<T> queryable)
    {
        return queryable.Skip((Page - 1) * Size).Take(Size);
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsLower(str[0]))
            return str;
        return char.ToLowerInvariant(str[0]) + str[1..];
    }

    private static string ToPascalCase(string str)
    {
        if (string.IsNullOrEmpty(str) || char.IsUpper(str[0]))
            return str;
        return char.ToUpperInvariant(str[0]) + str[1..];
    }

    private static Expression GetPropertyExpression(Type type, ParameterExpression param, string propertyName)
    {
        Expression expr = param;
        foreach (var part in propertyName.Split('.'))
        {
            var propInfo = expr.Type.GetProperty(part,
                BindingFlags.IgnoreCase |
                BindingFlags.Public |
                BindingFlags.Instance);

            if (propInfo == null)
                throw new RsqlParseException($"Property '{part}' not found on type '{expr.Type.Name}'");

            expr = Expression.Property(expr, propInfo);
        }
        return expr;
    }
}