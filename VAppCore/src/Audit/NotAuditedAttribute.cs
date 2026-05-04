namespace VAppCore;

/// <summary>
/// Excludes a property from the audit log diff. Useful for noisy fields like
/// LastSeenAt, LoginCount, or any high-frequency field whose changes aren't
/// worth recording in history.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotAuditedAttribute : Attribute { }
