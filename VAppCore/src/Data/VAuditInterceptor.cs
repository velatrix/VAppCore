using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace VAppCore;

/// <summary>
/// EF Core SaveChanges interceptor that populates audit fields on <see cref="IVAudited{TUserKey}"/>
/// entities, intercepts deletes of <see cref="ISoftDeletable"/> entities (turning them into modifies),
/// and assigns <see cref="ITenantScoped{TTenantKey}"/>.TenantId from the current user on Add.
/// Register via <c>DbContextOptionsBuilder.AddInterceptors(...)</c> or via the
/// <c>AddVAppCoreInterceptors</c> helper.
/// </summary>
public class VAuditInterceptor<TUserKey, TTenantKey> : ISaveChangesInterceptor
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    internal const string DeletedByPropertyName = "DeletedBy";

    private readonly ICurrentUser<TUserKey, TTenantKey>? _currentUser;

    public VAuditInterceptor(ICurrentUser<TUserKey, TTenantKey>? currentUser = null)
    {
        _currentUser = currentUser;
    }

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            ProcessEntities(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            ProcessEntities(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void ProcessEntities(DbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var hasUser = _currentUser is { IsAuthenticated: true };
        var userId = hasUser ? _currentUser!.UserId : default!;

        foreach (var entry in db.ChangeTracker.Entries())
        {
            // Audit fields on IVAudited entities
            if (entry.Entity is IVAudited<TUserKey> audited)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        audited.CreatedAt = now;
                        audited.UpdatedAt = now;
                        if (hasUser)
                        {
                            audited.CreatedBy = userId;
                            audited.UpdatedBy = userId;
                        }
                        break;

                    case EntityState.Modified:
                        audited.UpdatedAt = now;
                        if (hasUser)
                            audited.UpdatedBy = userId;
                        // Prevent overwriting CreatedAt/CreatedBy
                        entry.Property(nameof(IVAudited<TUserKey>.CreatedAt)).IsModified = false;
                        entry.Property(nameof(IVAudited<TUserKey>.CreatedBy)).IsModified = false;
                        break;
                }
            }

            // Soft delete interception
            if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable softDeletable })
            {
                entry.State = EntityState.Modified;
                softDeletable.IsDeleted = true;
                softDeletable.DeletedAt = now;

                // Set DeletedBy if the entity has such a property
                var deletedByProp = entry.Properties
                    .FirstOrDefault(p => p.Metadata.Name == DeletedByPropertyName);
                if (deletedByProp is not null && hasUser)
                    deletedByProp.CurrentValue = userId;
            }

            // Auto-set TenantId on new entities — only when not explicitly assigned.
            // Lets admin / migration code create rows on behalf of other tenants.
            if (entry is { State: EntityState.Added, Entity: ITenantScoped<TTenantKey> tenantEntity }
                && hasUser
                && EqualityComparer<TTenantKey>.Default.Equals(tenantEntity.TenantId, default!))
            {
                tenantEntity.TenantId = _currentUser!.TenantId;
            }
        }
    }
}
