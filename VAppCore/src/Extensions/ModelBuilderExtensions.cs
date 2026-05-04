using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace VAppCore;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Applies VAppCore's global query filters:
    /// - <see cref="ISoftDeletable"/> entities get <c>WHERE NOT IsDeleted</c>
    /// - <see cref="ITenantScoped{TTenantKey}"/> entities get <c>WHERE TenantId == dbContext.CurrentTenantId</c>,
    ///   but only if <paramref name="dbContext"/> implements <see cref="IVTenantContext{TTenantKey}"/>.
    /// Call from your DbContext's <c>OnModelCreating</c> after <c>base.OnModelCreating(modelBuilder)</c>.
    /// </summary>
    public static void ApplyVAppCoreFilters<TUserKey, TTenantKey>(
        this ModelBuilder modelBuilder,
        DbContext dbContext)
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        var supportsTenant = dbContext is IVTenantContext<TTenantKey>;

        // Concurrency-token configuration runs first (independent of filter logic below).
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(IConcurrent).IsAssignableFrom(clrType))
            {
                var prop = entityType.FindProperty(nameof(IConcurrent.RowVersion));
                if (prop != null)
                {
                    prop.IsConcurrencyToken = true;
                    prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate;
                }
            }

            if (typeof(IConcurrentXmin).IsAssignableFrom(clrType))
            {
                var prop = entityType.FindProperty(nameof(IConcurrentXmin.Xmin));
                if (prop != null)
                {
                    prop.SetColumnName("xmin");
                    prop.SetColumnType("xid");
                    prop.IsConcurrencyToken = true;
                    prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate;
                }
            }
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);
            var isTenantScoped = typeof(ITenantScoped<TTenantKey>).IsAssignableFrom(clrType) && supportsTenant;

            if (!isSoftDeletable && !isTenantScoped) continue;

            var parameter = Expression.Parameter(clrType, "e");
            Expression? filter = null;

            if (isSoftDeletable)
            {
                var isDeletedProp = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                filter = Expression.Not(isDeletedProp);
            }

            if (isTenantScoped)
            {
                var tenantIdProp = Expression.Property(parameter, nameof(ITenantScoped<TTenantKey>.TenantId));
                var dbContextExpr = Expression.Constant(dbContext);
                var tenantContextType = typeof(IVTenantContext<TTenantKey>);
                var castExpr = Expression.Convert(dbContextExpr, tenantContextType);
                var currentTenantProp = Expression.Property(castExpr, nameof(IVTenantContext<TTenantKey>.CurrentTenantId));

                var tenantFilter = Expression.Equal(tenantIdProp, currentTenantProp);

                filter = filter is not null
                    ? Expression.AndAlso(filter, tenantFilter)
                    : tenantFilter;
            }

            if (filter is not null)
            {
                var lambda = Expression.Lambda(filter, parameter);
                entityType.SetQueryFilter(lambda);
            }
        }
    }
}
