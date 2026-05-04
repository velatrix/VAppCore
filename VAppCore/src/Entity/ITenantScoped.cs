namespace VAppCore;

/// <summary>
/// Opt-in interface for multi-tenancy. Entities implementing this are
/// automatically scoped by TenantId — ApplyVAppCoreFilters applies a global query
/// filter (when the DbContext implements IVTenantContext) and VAuditInterceptor
/// auto-sets TenantId on new entities from CurrentUser.
/// </summary>
public interface ITenantScoped<TTenantKey>
{
    TTenantKey TenantId { get; set; }
}
