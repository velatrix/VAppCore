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

    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    /// <summary>
    /// Gets the page number from the "page" query parameter (1-based, defaults to 1).
    /// Used only when the filter has EnablePageNavigation and the request includes ?page=N.
    /// </summary>
    public int Page => int.TryParse(Get("page"), out var p) && p > 0 ? p : 1;

    /// <summary>
    /// Gets the row limit from the "limit" query parameter (defaults to 20, max 100).
    /// Caller-facing rename of "size" — same semantic.
    /// </summary>
    public int Limit => int.TryParse(Get("limit"), out var s) && s > 0 ? Math.Min(s, MaxLimit) : DefaultLimit;

    /// <summary>
    /// Gets the cursor from the "cursor" query parameter (forward direction), or null.
    /// </summary>
    public string? Cursor
    {
        get
        {
            var c = Get("cursor");
            return string.IsNullOrEmpty(c) ? null : c;
        }
    }

    /// <summary>
    /// Gets the cursor from the "before" query parameter (backward direction), or null.
    /// </summary>
    public string? Before
    {
        get
        {
            var c = Get("before");
            return string.IsNullOrEmpty(c) ? null : c;
        }
    }

    /// <summary>
    /// True when the request explicitly asked for offset-mode pagination (?page=N).
    /// </summary>
    public bool IsPageRequested => Query.ContainsKey("page");

    /// <summary>
    /// True when the request explicitly provided a non-empty forward cursor (?cursor=X).
    /// </summary>
    public bool IsCursorRequested => !string.IsNullOrEmpty(Cursor);

    /// <summary>
    /// True when the request explicitly provided a backward cursor (?before=X).
    /// </summary>
    public bool IsBeforeRequested => !string.IsNullOrEmpty(Before);

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
        queryable = queryable.Skip((Page - 1) * Limit).Take(Limit);

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
        queryable = queryable.Skip((Page - 1) * Limit).Take(Limit);

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
        queryable = queryable.Skip((Page - 1) * Limit).Take(Limit);

        // Get fields to project
        var fields = GetEffectiveSelectFields();
        if (fields.Count == 0)
        {
            // No selectable fields configured - return empty
            return new VPagedResponse<object>
            {
                Items = [],
                Page = Page,
                Limit = Limit,
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
            Limit = Limit,
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
        return queryable.Skip((Page - 1) * Limit).Take(Limit);
    }

    // ── Cursor pagination ──

    /// <summary>
    /// Cursor-mode pagination. Reads <c>?cursor=X</c> (forward) or <c>?before=X</c> (backward),
    /// applies RSQL filter and the cursor WHERE clause, sorts by the request's sort spec
    /// (with Id appended as a stable tiebreaker), and returns up to <c>Limit</c> items along
    /// with next/previous cursors and a hasMore indicator.
    /// </summary>
    public async Task<VPagedResponse<T>> ApplyWithCursorAsync<T>(
        IQueryable<T> queryable,
        CursorCodec codec,
        CancellationToken ct = default) where T : class
    {
        if (IsCursorRequested && IsPageRequested)
            throw new RsqlValidationException("Cannot specify both ?cursor and ?page in the same request.");
        if (IsCursorRequested && IsBeforeRequested)
            throw new RsqlValidationException("Cannot specify both ?cursor (forward) and ?before (backward) in the same request.");

        var direction = IsBeforeRequested ? CursorDirection.Backward : CursorDirection.Forward;
        var cursorString = IsBeforeRequested ? Before : Cursor;

        // Build effective sort: user/default sort + Id tiebreaker
        var sortFields = ParseSortFields().ToList();
        if (!sortFields.Any(f => f.FieldName.Equals("Id", StringComparison.OrdinalIgnoreCase)))
            sortFields.Add(new SortField("Id", Descending: false));

        // Validate sortable
        if (FilterConfig != null)
            FilterConfig.ValidateSortFields(sortFields.Select(f => f.FieldName));

        // Reject custom-field cursor sorts — their expressions can't be reliably reproduced
        // in the cursor's WHERE clause (computed values don't have a property accessor on T).
        if (FilterConfig != null)
        {
            var customSorts = sortFields
                .Where(f => FilterConfig.CustomFields.ContainsKey(f.FieldName))
                .Select(f => f.FieldName)
                .ToList();
            if (customSorts.Count > 0)
                throw new RsqlValidationException(
                    $"Cannot use custom field(s) as cursor sort: {string.Join(", ", customSorts)}. " +
                    "Custom fields (CountOf, FromNavigation, WithExpression, WithNullCheck) are computed " +
                    "and can't be reproduced in cursor pagination's WHERE clause. Either sort by a " +
                    "real entity property, or use offset pagination via ?page=N.");
        }

        // Decode cursor (if any). Mismatched sort → ignore cursor (treat as first page in current sort).
        CursorPayload? payload = null;
        if (!string.IsNullOrEmpty(cursorString))
        {
            payload = codec.Decode(cursorString);
            if (!CursorSortMatches(payload.Sort, sortFields))
            {
                payload = null;
                direction = CursorDirection.Forward; // sort changed → treat as fresh page 1
            }
        }

        // Auto-include navigation properties
        foreach (var include in GetRequiredIncludes())
            queryable = queryable.Include(include);

        // RSQL filter
        var filterExpr = GetFilterExpression<T>();
        if (filterExpr != null)
            queryable = queryable.Where(filterExpr);

        // Cursor WHERE
        if (payload != null)
        {
            var values = ConvertCursorValues<T>(payload.Values, sortFields);
            var cursorWhere = BuildCursorWhereExpression<T>(sortFields, values, direction == CursorDirection.Forward);
            queryable = queryable.Where(cursorWhere);
        }

        // ORDER BY (reversed for backward direction, then result list reversed at the end)
        queryable = ApplyOrderBy(queryable, sortFields, reverse: direction == CursorDirection.Backward);

        // Take Limit + 1 to detect hasMore
        var page = await queryable.Take(Limit + 1).ToListAsync(ct);
        var hasMore = page.Count > Limit;
        if (hasMore) page.RemoveAt(Limit);

        // Backward: reverse to display order
        if (direction == CursorDirection.Backward)
            page.Reverse();

        // Build response cursors
        string? nextCursor = null;
        string? previousCursor = null;

        if (page.Count > 0)
        {
            // nextCursor: encoded position of LAST item, only when more items follow
            var morePagesForward = direction == CursorDirection.Forward
                ? hasMore
                : true; // if we just paged backward, going forward from here always works
            if (morePagesForward)
                nextCursor = codec.Encode(BuildPayload(page[^1], sortFields));

            // previousCursor: encoded position of FIRST item, only when there's a prior page.
            // If we used a cursor at all (forward or backward), there's a prior page by definition.
            // First-page-with-no-cursor case: previousCursor stays null (the "is page 1" signal).
            var hasPrior = direction == CursorDirection.Forward
                ? payload != null
                : hasMore; // backward + hasMore means there are more rows beyond current page going backward
            if (hasPrior)
                previousCursor = codec.Encode(BuildPayload(page[0], sortFields));
        }

        return new VPagedResponse<T>
        {
            Items = page,
            Limit = Limit,
            HasMore = hasMore,
            NextCursor = nextCursor,
            PreviousCursor = previousCursor
        };
    }

    /// <summary>
    /// Cursor-mode pagination with field projection (per the filter's Selectable fields).
    /// Same semantics as <see cref="ApplyWithCursorAsync{T}"/>, but returns
    /// <c>VPagedResponse&lt;object&gt;</c> with projected anonymous objects instead of
    /// raw entities. Cursors are computed from the FULL entity (so sort fields
    /// don't need to be selectable).
    /// </summary>
    public async Task<VPagedResponse<object>> ApplyWithCursorProjectionAsync<T>(
        IQueryable<T> queryable,
        CursorCodec codec,
        CancellationToken ct = default) where T : class
    {
        // Reuse the typed cursor flow to get entities + cursors.
        var typed = await ApplyWithCursorAsync(queryable, codec, ct);

        // Project items in-memory using the same dynamic-select expression as offset mode.
        var fields = GetEffectiveSelectFields();
        List<object> projectedItems;
        if (fields.Count == 0)
        {
            projectedItems = [];
        }
        else
        {
            var selectExpression = BuildDynamicSelectExpression(fields);
            // Project the materialized list via System.Linq.Dynamic.Core
            projectedItems = typed.Items.AsQueryable().Select(selectExpression).ToDynamicList().Cast<object>().ToList();
        }

        return new VPagedResponse<object>
        {
            Items = projectedItems,
            Limit = typed.Limit,
            HasMore = typed.HasMore,
            NextCursor = typed.NextCursor,
            PreviousCursor = typed.PreviousCursor
        };
    }

    private enum CursorDirection { Forward, Backward }

    private record SortField(string FieldName, bool Descending);

    private List<SortField> ParseSortFields()
    {
        var sortString = EffectiveSort;
        var result = new List<SortField>();
        if (string.IsNullOrWhiteSpace(sortString)) return result;

        foreach (var raw in sortString.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var descending = trimmed.StartsWith('-');
            var name = trimmed.TrimStart('+', '-');
            if (FilterConfig != null) name = FilterConfig.ResolveFieldName(name);
            result.Add(new SortField(name, descending));
        }
        return result;
    }

    private static bool CursorSortMatches(string cursorSort, List<SortField> requestSort)
    {
        // Compare canonical form: each field with sign prefix, comma-separated
        var canonicalRequest = string.Join(",", requestSort.Select(f => (f.Descending ? "-" : "+") + f.FieldName));
        return string.Equals(cursorSort, canonicalRequest, StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalSort(List<SortField> fields)
        => string.Join(",", fields.Select(f => (f.Descending ? "-" : "+") + f.FieldName));

    private static object?[] ConvertCursorValues<T>(System.Text.Json.JsonElement[] elements, List<SortField> sortFields)
    {
        if (elements.Length != sortFields.Count)
            throw new CursorDecodeException(
                $"Cursor has {elements.Length} values, expected {sortFields.Count} (one per sort field including Id tiebreaker).");

        var values = new object?[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            var prop = typeof(T).GetProperty(sortFields[i].FieldName,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
                throw new CursorDecodeException($"Cursor field '{sortFields[i].FieldName}' not found on {typeof(T).Name}.");
            values[i] = ConvertJsonElement(elements[i], prop.PropertyType);
        }
        return values;
    }

    private static object? ConvertJsonElement(System.Text.Json.JsonElement el, Type targetType)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t == typeof(string)) return el.GetString();
        if (t == typeof(int)) return el.GetInt32();
        if (t == typeof(long)) return el.GetInt64();
        if (t == typeof(short)) return el.GetInt16();
        if (t == typeof(decimal)) return el.GetDecimal();
        if (t == typeof(double)) return el.GetDouble();
        if (t == typeof(float)) return el.GetSingle();
        if (t == typeof(bool)) return el.GetBoolean();
        if (t == typeof(Guid)) return el.GetGuid();
        if (t == typeof(DateTime)) return el.GetDateTime();
        if (t == typeof(DateTimeOffset)) return el.GetDateTimeOffset();
        if (t.IsEnum)
        {
            return el.ValueKind == System.Text.Json.JsonValueKind.Number
                ? Enum.ToObject(t, el.GetInt32())
                : Enum.Parse(t, el.GetString()!);
        }
        throw new CursorDecodeException($"Unsupported cursor field type: {t.Name}");
    }

    private static Expression<Func<T, bool>> BuildCursorWhereExpression<T>(
        List<SortField> sortFields,
        object?[] cursorValues,
        bool forward)
    {
        var param = Expression.Parameter(typeof(T), "x");
        Expression? combined = null;

        for (int i = 0; i < sortFields.Count; i++)
        {
            Expression? clause = null;

            // Equal on fields 0..i-1 (must hold for this row to be "tied" at field i)
            for (int j = 0; j < i; j++)
            {
                var propJ = GetPropertyExpression(typeof(T), param, sortFields[j].FieldName);
                var eq = BuildEqualityForCursor(propJ, cursorValues[j]);
                clause = clause is null ? eq : Expression.AndAlso(clause, eq);
            }

            // Strict comparison on field i — direction + null-aware
            var propI = GetPropertyExpression(typeof(T), param, sortFields[i].FieldName);
            var strict = BuildStrictComparisonForCursor(propI, cursorValues[i], sortFields[i], forward);

            // If strict is "always false" (e.g. cursor at NULL going forward in NULLS-LAST), this
            // clause contributes nothing — skip it instead of adding `false OR ...`.
            if (strict is ConstantExpression cExpr && cExpr.Value is false)
            {
                // No-op for this step.
                continue;
            }

            clause = clause is null ? strict : Expression.AndAlso(clause, strict);
            combined = combined is null ? clause : Expression.OrElse(combined, clause);
        }

        return Expression.Lambda<Func<T, bool>>(combined ?? Expression.Constant(false), param);
    }

    /// <summary>Builds the equality clause for "tied at this field" used as a prefix in the next OR step.</summary>
    private static Expression BuildEqualityForCursor(Expression field, object? cursorValue)
    {
        var nullable = IsNullable(field.Type);
        if (!nullable)
            return Expression.Equal(field, Expression.Constant(cursorValue, field.Type));

        if (cursorValue is null)
            return Expression.Equal(field, Expression.Constant(null, field.Type));

        // Non-null cursor on nullable field: field IS NOT NULL AND field == val
        var notNull = Expression.NotEqual(field, Expression.Constant(null, field.Type));
        var eq = Expression.Equal(field, Expression.Constant(cursorValue, field.Type));
        return Expression.AndAlso(notNull, eq);
    }

    /// <summary>
    /// Builds the strict (greater-than / less-than) comparison for cursor pagination,
    /// handling NULLs as "always last" regardless of sort direction.
    /// Returns Expression.Constant(false) when the comparison contributes no rows
    /// (e.g. cursor at NULL going forward — there's nothing after NULLs in NULLS-LAST).
    /// </summary>
    private static Expression BuildStrictComparisonForCursor(
        Expression field, object? cursorValue, SortField sortField, bool forward)
    {
        var greaterThan = forward ^ sortField.Descending;  // forward+asc → >, forward+desc → <, etc.
        var nullable = IsNullable(field.Type);

        // Cursor at NULL position
        if (cursorValue is null && nullable)
        {
            if (forward)
            {
                // NULLS LAST → going forward from NULL, no rows come after (other NULLs are tied,
                // handled by the next field's tiebreaker step).
                return Expression.Constant(false);
            }
            else
            {
                // Going backward from NULL → all non-null rows are before (in NULLS-LAST ordering).
                return Expression.NotEqual(field, Expression.Constant(null, field.Type));
            }
        }

        // Cursor at non-null value
        var direct = BuildComparison(field, Expression.Constant(cursorValue, field.Type), greaterThan);

        if (!nullable) return direct;

        var notNull = Expression.NotEqual(field, Expression.Constant(null, field.Type));
        var directInNonNull = Expression.AndAlso(notNull, direct);

        if (forward)
        {
            // Forward in non-null section, OR jump to NULL section (NULLs come after)
            var isNull = Expression.Equal(field, Expression.Constant(null, field.Type));
            return Expression.OrElse(directInNonNull, isNull);
        }
        // Backward: NULL section is "after" current position, don't include
        return directInNonNull;
    }

    /// <summary>
    /// Builds a "greater than" or "less than" expression that works for primitive
    /// types (via direct operator) and for string / Guid / other IComparable types
    /// (via CompareTo). EF Core translates both forms to native SQL comparisons.
    /// </summary>
    private static Expression BuildComparison(Expression left, Expression right, bool greaterThan)
    {
        var type = left.Type;
        try
        {
            return greaterThan
                ? Expression.GreaterThan(left, right)
                : Expression.LessThan(left, right);
        }
        catch (InvalidOperationException)
        {
            var compareTo = type.GetMethod("CompareTo", [type])
                ?? throw new InvalidOperationException(
                    $"Cannot build cursor comparison for type {type.Name} — no operator and no CompareTo({type.Name}) method.");
            var compareResult = Expression.Call(left, compareTo, right);
            var zero = Expression.Constant(0);
            return greaterThan
                ? Expression.GreaterThan(compareResult, zero)
                : Expression.LessThan(compareResult, zero);
        }
    }

    private static IQueryable<T> ApplyOrderBy<T>(IQueryable<T> queryable, List<SortField> sortFields, bool reverse)
    {
        IOrderedQueryable<T>? ordered = null;
        for (int i = 0; i < sortFields.Count; i++)
        {
            var field = sortFields[i];
            var descending = reverse ? !field.Descending : field.Descending;

            var param = Expression.Parameter(typeof(T), "x");
            var prop = GetPropertyExpression(typeof(T), param, field.FieldName);
            var nullable = IsNullable(prop.Type);

            // Force NULLS LAST always (regardless of asc/desc) by sorting on (field == null) first.
            // false (0) sorts before true (1), so non-null comes first.
            // For backward direction we still want NULLs at the end of the (reversed-back) result,
            // so we still want non-null first in the forward query — same comparison.
            if (nullable)
            {
                var nullCheck = Expression.Equal(prop, Expression.Constant(null, prop.Type));
                var nullCheckLambda = Expression.Lambda(nullCheck, param);
                var nullCheckMethodName = ordered == null ? "OrderBy" : "ThenBy";
                var nullCheckMethod = typeof(Queryable).GetMethods()
                    .First(m => m.Name == nullCheckMethodName && m.GetParameters().Length == 2)
                    .MakeGenericMethod(typeof(T), typeof(bool));
                ordered = (IOrderedQueryable<T>)nullCheckMethod.Invoke(null, [ordered ?? queryable, nullCheckLambda])!;
            }

            var lambda = Expression.Lambda(prop, param);
            var methodName = ordered == null
                ? (descending ? "OrderByDescending" : "OrderBy")
                : (descending ? "ThenByDescending" : "ThenBy");

            var method = typeof(Queryable).GetMethods()
                .First(m => m.Name == methodName && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(T), prop.Type);

            ordered = (IOrderedQueryable<T>)method.Invoke(null, [ordered ?? queryable, lambda])!;
        }
        return ordered ?? queryable;
    }

    private static bool IsNullable(Type t) =>
        !t.IsValueType || Nullable.GetUnderlyingType(t) != null;

    private static CursorPayload BuildPayload<T>(T entity, List<SortField> sortFields)
    {
        var values = new System.Text.Json.JsonElement[sortFields.Count];
        for (int i = 0; i < sortFields.Count; i++)
        {
            var prop = typeof(T)!.GetProperty(sortFields[i].FieldName,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Sort field '{sortFields[i].FieldName}' not found on {typeof(T).Name}.");
            var value = prop.GetValue(entity);
            values[i] = System.Text.Json.JsonSerializer.SerializeToElement(value);
        }
        return new CursorPayload
        {
            Sort = CanonicalSort(sortFields),
            Values = values
        };
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