namespace VAppCore;

/// <summary>
/// Opt-in marker for entities tracked by the VAppCore audit log. Entities NOT
/// implementing this interface are completely ignored by AuditLogInterceptor
/// (zero overhead). To exclude individual properties from the diff, decorate them with <see cref="NotAuditedAttribute"/>.
/// </summary>
public interface IAuditedEntity { }
