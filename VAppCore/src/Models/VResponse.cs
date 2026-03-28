namespace VAppCore;

/// <summary>
/// Wraps a mapped response. Controllers must return this (or VPagedResponse)
/// for object responses — raw entities and unmapped objects are blocked by VResponseFilter.
/// </summary>
public class VResponse
{
    internal object Data { get; }

    private VResponse(object data) => Data = data;

    /// <summary>
    /// Maps a single entity to a response shape.
    /// </summary>
    public static VResponse Map<T>(T entity, Func<T, object> map) => new(map(entity));

    /// <summary>
    /// Maps a collection of entities to a response shape.
    /// </summary>
    public static VResponse MapList<T>(IEnumerable<T> entities, Func<T, object> map)
        => new(entities.Select(map).ToList());
}
