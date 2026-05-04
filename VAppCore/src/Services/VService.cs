using Microsoft.EntityFrameworkCore;

namespace VAppCore;

public abstract class VService<TEntity, TKey, TUserKey, TTenantKey>
    where TEntity : VEntity<TKey, TUserKey, TTenantKey>
    where TKey : IEquatable<TKey>
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    /// <summary>
    /// Auto-injected by AddVService/AddVServices. Do not set manually.
    /// </summary>
    public DbContext Db { get; internal set; } = null!;

    /// <summary>
    /// Auto-injected by AddVService/AddVServices. Do not set manually.
    /// </summary>
    public ICurrentUser<TUserKey, TTenantKey> CurrentUser { get; internal set; } = null!;

    /// <summary>
    /// Auto-injected by AddVService/AddVServices. Do not set manually.
    /// </summary>
    public CursorCodec CursorCodec { get; internal set; } = null!;

    protected DbSet<TEntity> Set => Db.Set<TEntity>();

    /// <summary>
    /// Gets entity by ID. Throws NotFoundError (404) if not found.
    /// Pass configure to add includes or extra conditions.
    /// </summary>
    public virtual async Task<TEntity> GetByIdAsync(
        TKey id,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? configure = null)
    {
        if (configure is null)
        {
            return await Set.FindAsync(id)
                   ?? throw new NotFoundError(new ErrorObject
                   {
                       Message = $"{typeof(TEntity).Name} not found",
                       MessageKey = "server.errors.notFound"
                   });
        }

        var entity = await configure(Set.AsQueryable())
            .FirstOrDefaultAsync(e => e.Id.Equals(id));

        return entity ?? throw new NotFoundError(new ErrorObject
        {
            Message = $"{typeof(TEntity).Name} not found",
            MessageKey = "server.errors.notFound"
        });
    }

    /// <summary>
    /// Gets entity by ID. Returns null if not found.
    /// </summary>
    public virtual async Task<TEntity?> FindByIdAsync(TKey id)
    {
        return await Set.FindAsync(id);
    }

    /// <summary>
    /// Gets paged results using VQueryParser. Branches on the request:
    /// — <c>?cursor=X</c> or <c>?before=X</c> → cursor mode (fast, no COUNT)
    /// — <c>?page=N</c> → offset mode (requires the filter to opt in via <c>EnablePageNavigation()</c>)
    /// — neither → cursor mode default (first page)
    /// Both modes return projected fields per the filter's <c>Selectable</c> declarations.
    /// </summary>
    public virtual async Task<VPagedResponse<object>> GetPagedAsync(
        VQueryParser parser,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? configure = null)
    {
        var query = Set.AsQueryable();

        if (configure is not null)
            query = configure(query);

        // Page mode: requires filter opt-in
        if (parser.IsPageRequested)
        {
            if (parser.FilterConfig?.PageNavigationEnabled != true)
                throw new RsqlValidationException(
                    "?page=N is not allowed on this endpoint. The filter must opt in via EnablePageNavigation().");
            return await parser.ApplyWithProjectionAsync<TEntity>(query);
        }

        // Cursor mode (default)
        return await parser.ApplyWithCursorProjectionAsync<TEntity>(query, CursorCodec);
    }

    /// <summary>
    /// Deletes entity by ID. ISoftDeletable entities are soft-deleted by VAuditInterceptor.
    /// </summary>
    public virtual async Task DeleteAsync(TKey id)
    {
        var entity = await GetByIdAsync(id);
        Set.Remove(entity);
        await SaveAsync();
    }

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    protected async Task SaveAsync()
    {
        await Db.SaveChangesAsync();
    }
}
