namespace VAppCore;

/// <summary>
/// Per-field diff entry stored in AuditLog.Changes.
/// For Add: Old is null. For Delete: New is null. For Modify: both populated.
/// </summary>
public sealed record AuditChange(object? Old, object? New);
