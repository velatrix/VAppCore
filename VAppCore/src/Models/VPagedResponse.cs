namespace VAppCore;

/// <summary>
/// Unified paginated response. The same shape is used for cursor-mode and
/// offset-mode (page-nav) requests; some fields are populated only in one
/// mode or the other.
/// </summary>
public class VPagedResponse<T>
{
    public List<T> Items { get; set; } = [];
    public int Limit { get; set; }

    /// <summary>True if there are more items beyond this response.</summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// Cursor for the next set of items. Null when there are no more items.
    /// Populated in both cursor mode and offset mode (so clients can switch).
    /// </summary>
    public string? NextCursor { get; set; }

    /// <summary>
    /// Cursor for the previous set of items. Null on the first page.
    /// Populated in both cursor mode and offset mode.
    /// In cursor mode, a null value AFTER the client sent a cursor means
    /// the server discarded the cursor (typically because the sort changed).
    /// </summary>
    public string? PreviousCursor { get; set; }

    /// <summary>
    /// Current page number (1-based). Populated only when ?page=N was requested
    /// on a filter with EnablePageNavigation.
    /// </summary>
    public int? Page { get; set; }

    /// <summary>
    /// Total item count across all pages. Populated only in offset mode (a COUNT
    /// query runs). Null in cursor mode by design — preserves cursor-mode perf.
    /// </summary>
    public int? TotalItems { get; set; }

    /// <summary>
    /// Total page count, computed from TotalItems / Limit. Null in cursor mode.
    /// </summary>
    public int? TotalPages => TotalItems is int total && Limit > 0
        ? (int)Math.Ceiling((double)total / Limit)
        : null;
}
