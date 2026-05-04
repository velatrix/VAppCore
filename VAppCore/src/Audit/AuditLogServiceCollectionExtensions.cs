using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VAppCore;

public static class AuditLogServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AuditLogInterceptor{TUserKey, TTenantKey}"/> (per-scope so the current user
    /// is per-request) and <see cref="IAuditLog"/> as the default <see cref="AuditLogService"/> backed by
    /// <typeparamref name="TDbContext"/>. Pair with <c>options.AddVAppCoreAuditInterceptors&lt;TUserKey, TTenantKey&gt;(sp)</c>
    /// on the DbContext options.
    /// </summary>
    public static IServiceCollection AddVAppCoreAuditLog<TDbContext, TUserKey, TTenantKey>(
        this IServiceCollection services)
        where TDbContext : DbContext
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        services.TryAddScoped<AuditLogInterceptor<TUserKey, TTenantKey>>(sp =>
            new AuditLogInterceptor<TUserKey, TTenantKey>(
                sp.GetService<ICurrentUser<TUserKey, TTenantKey>>()));

        services.TryAddScoped<IAuditLog>(sp =>
            new AuditLogService(sp.GetRequiredService<TDbContext>()));

        return services;
    }
}
