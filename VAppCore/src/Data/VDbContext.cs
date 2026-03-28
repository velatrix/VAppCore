using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore;

public abstract class VDbContext<TKey, TUserKey, TTenantKey> : DbContext
    where TKey : IEquatable<TKey>
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    public IServiceProvider ServiceProvider { get; }

    private readonly ICurrentUser<TUserKey, TTenantKey>? _currentUser;

    protected VDbContext(DbContextOptions options, IServiceProvider serviceProvider) : base(options)
    {
        ServiceProvider = serviceProvider;
        _currentUser = serviceProvider.GetService<ICurrentUser<TUserKey, TTenantKey>>();
    }

    /// <summary>
    /// Current tenant ID from the authenticated user. Used by global query filters.
    /// </summary>
    protected TTenantKey CurrentTenantId => _currentUser != null ? _currentUser.TenantId : default!;

    // ── SaveChanges interception ──

    public override int SaveChanges()
    {
        ProcessEntities();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ProcessEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ProcessEntities();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ProcessEntities();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ProcessEntities()
    {
        var now = DateTimeOffset.UtcNow;
        var hasUser = _currentUser is { IsAuthenticated: true };
        var userId = hasUser ? _currentUser!.UserId : default!;

        foreach (var entry in ChangeTracker.Entries())
        {
            // Audit fields on VEntity
            if (entry.Entity is VEntity<TKey, TUserKey, TTenantKey> entity)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entity.CreatedAt = now;
                        entity.UpdatedAt = now;
                        if (hasUser)
                        {
                            entity.CreatedBy = userId;
                            entity.UpdatedBy = userId;
                        }
                        break;

                    case EntityState.Modified:
                        entity.UpdatedAt = now;
                        if (hasUser)
                            entity.UpdatedBy = userId;
                        // Prevent overwriting CreatedAt/CreatedBy
                        entry.Property(nameof(VEntity<TKey, TUserKey, TTenantKey>.CreatedAt)).IsModified = false;
                        entry.Property(nameof(VEntity<TKey, TUserKey, TTenantKey>.CreatedBy)).IsModified = false;
                        break;
                }
            }

            // Soft delete interception
            if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable softDeletable })
            {
                entry.State = EntityState.Modified;
                softDeletable.IsDeleted = true;
                softDeletable.DeletedAt = now;

                // Try to set DeletedBy if property exists on the entity
                var deletedByProp = entry.Properties
                    .FirstOrDefault(p => p.Metadata.Name == "DeletedBy");
                if (deletedByProp is not null && hasUser)
                    deletedByProp.CurrentValue = userId;
            }

            // Auto-set TenantId on new entities
            if (entry is { State: EntityState.Added, Entity: ITenantScoped<TTenantKey> tenantEntity } && hasUser)
            {
                tenantEntity.TenantId = _currentUser!.TenantId;
            }
        }
    }

    // ── Model configuration ──

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);
            var isTenantScoped = typeof(ITenantScoped<TTenantKey>).IsAssignableFrom(clrType);

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
                // Build: e.TenantId == this.CurrentTenantId
                var tenantIdProp = Expression.Property(parameter, nameof(ITenantScoped<TTenantKey>.TenantId));
                var dbContextExpr = Expression.Constant(this);
                var currentTenantProp = Expression.Property(dbContextExpr,
                    typeof(VDbContext<TKey, TUserKey, TTenantKey>)
                        .GetProperty(nameof(CurrentTenantId), BindingFlags.NonPublic | BindingFlags.Instance)!);

                // Handle nullable comparison
                var tenantFilter = Expression.Equal(
                    tenantIdProp,
                    Expression.Convert(currentTenantProp, tenantIdProp.Type));

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

    // ── Transaction support ──

    /// <summary>
    /// Wraps the action in a transaction. If already in a transaction, reuses it.
    /// If the action throws, rolls back. Otherwise commits.
    /// </summary>
    public async Task<T> TransactionAsync<T>(Func<Task<T>> action)
    {
        if (Database.CurrentTransaction is not null)
            return await action();

        using var trx = await Database.BeginTransactionAsync();
        try
        {
            var result = await action();
            await trx.CommitAsync();
            return result;
        }
        catch
        {
            await trx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Wraps the action in a transaction. If already in a transaction, reuses it.
    /// </summary>
    public async Task TransactionAsync(Func<Task> action)
    {
        if (Database.CurrentTransaction is not null)
        {
            await action();
            return;
        }

        using var trx = await Database.BeginTransactionAsync();
        try
        {
            await action();
            await trx.CommitAsync();
        }
        catch
        {
            await trx.RollbackAsync();
            throw;
        }
    }
}
