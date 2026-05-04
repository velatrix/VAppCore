namespace VAppCore;

/// <summary>
/// Implemented by DbContexts that participate in tenant scoping. The
/// <see cref="ModelBuilderExtensions.ApplyVAppCoreFilters{TUserKey, TTenantKey}"/>
/// extension uses this to resolve the current tenant when building the global
/// query filter for <see cref="ITenantScoped{TTenantKey}"/> entities.
/// Implement this on your DbContext (typically by reading CurrentUser.TenantId
/// from a DI-injected ICurrentUser) when you need tenant-scoped queries.
/// </summary>
public interface IVTenantContext<TTenantKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    TTenantKey CurrentTenantId { get; }
}
