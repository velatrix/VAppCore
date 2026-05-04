namespace VAppCore;

/// <summary>The type of change recorded in an <see cref="AuditLog"/> row.</summary>
public enum AuditAction
{
    Unknown = 0,
    Add = 1,
    Modify = 2,
    Delete = 3
}
