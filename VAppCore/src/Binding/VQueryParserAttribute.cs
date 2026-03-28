namespace VAppCore;

/// <summary>
/// Enables VQueryParser model binding with optional field filtering configuration.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter)]
public class UseVQueryParserAttribute : Attribute
{
    /// <summary>
    /// The filter configuration type that defines allowed fields.
    /// Must inherit from VQueryFilter&lt;T&gt;.
    /// </summary>
    public Type? FilterType { get; }

    public UseVQueryParserAttribute() { }

    public UseVQueryParserAttribute(Type filterType)
    {
        if (!IsValidFilterType(filterType))
        {
            throw new ArgumentException(
                $"FilterType must inherit from VQueryFilter<T>. Got: {filterType.Name}",
                nameof(filterType));
        }
        FilterType = filterType;
    }

    private static bool IsValidFilterType(Type type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VQueryFilter<>))
                return true;
            current = current.BaseType;
        }
        return false;
    }
}

/// <summary>
/// Enables VQueryParser model binding with type-safe field filtering configuration.
/// </summary>
/// <typeparam name="TFilter">The filter configuration type</typeparam>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter)]
public class UseVQueryParserAttribute<TFilter> : UseVQueryParserAttribute
    where TFilter : IVQueryFilter, new()
{
    public UseVQueryParserAttribute() : base(typeof(TFilter)) { }
}