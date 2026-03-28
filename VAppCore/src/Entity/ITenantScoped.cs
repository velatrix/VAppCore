namespace VAppCore;

/// <summary>
/// Opt-in interface for multi-tenancy. Entities implementing this are
/// automatically scoped by TenantId — VDbContext applies a global query
/// filter and auto-sets TenantId on new entities from CurrentUser.
/// </summary>
public interface ITenantScoped<TTenantKey>
{
    TTenantKey TenantId { get; set; }
}
