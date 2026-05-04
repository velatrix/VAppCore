using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore;

public static class AuditLogDbContextOptionsExtensions
{
    /// <summary>
    /// Wires <see cref="AuditLogInterceptor{TUserKey, TTenantKey}"/> onto the DbContext options.
    /// Resolves <see cref="ICurrentUser{TUserKey, TTenantKey}"/> from <paramref name="serviceProvider"/>
    /// (pass the per-request scope provider — the form available inside
    /// <c>services.AddDbContext&lt;TDb&gt;((sp, opts) =&gt; ...)</c>).
    /// </summary>
    public static DbContextOptionsBuilder AddVAppCoreAuditInterceptors<TUserKey, TTenantKey>(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        // Resolve from DI (registered as Scoped by AddVAppCoreAuditLog) so the interceptor
        // shares lifetime + ICurrentUser with the per-request scope. The (sp, opts) form of
        // AddDbContext gives us the per-scope provider here.
        var interceptor = serviceProvider.GetRequiredService<AuditLogInterceptor<TUserKey, TTenantKey>>();
        builder.AddInterceptors(interceptor);
        return builder;
    }

    public static DbContextOptionsBuilder<TContext> AddVAppCoreAuditInterceptors<TContext, TUserKey, TTenantKey>(
        this DbContextOptionsBuilder<TContext> builder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        AddVAppCoreAuditInterceptors<TUserKey, TTenantKey>((DbContextOptionsBuilder)builder, serviceProvider);
        return builder;
    }
}
