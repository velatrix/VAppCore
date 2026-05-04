using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace VAppCore;

public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Wires VAppCore on the options: registers the audit interceptor and replaces
    /// EF Core's <see cref="IModelCustomizer"/> with one that applies VAppCore's
    /// global query filters after the consumer's <c>OnModelCreating</c>.
    /// The consumer's DbContext class needs no VAppCore-specific overrides.
    /// </summary>
    public static DbContextOptionsBuilder UseVAppCore<TUserKey, TTenantKey>(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        var currentUser = serviceProvider.GetService<ICurrentUser<TUserKey, TTenantKey>>();
        builder.AddInterceptors(new VAuditInterceptor<TUserKey, TTenantKey>(currentUser));
        builder.ReplaceService<IModelCustomizer, VAppCoreModelCustomizer<TUserKey, TTenantKey>>();
        return builder;
    }

    /// <summary>
    /// Generic-options-builder overload of <see cref="UseVAppCore{TUserKey, TTenantKey}"/>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseVAppCore<TContext, TUserKey, TTenantKey>(
        this DbContextOptionsBuilder<TContext> builder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        UseVAppCore<TUserKey, TTenantKey>((DbContextOptionsBuilder)builder, serviceProvider);
        return builder;
    }


    /// <summary>
    /// Registers <see cref="VAuditInterceptor{TUserKey, TTenantKey}"/> on the options.
    /// Resolves <see cref="ICurrentUser{TUserKey, TTenantKey}"/> from <paramref name="serviceProvider"/>;
    /// pass the per-request scope provider for correct per-request user resolution
    /// (e.g. via <c>services.AddDbContext&lt;TDb&gt;((sp, options) =&gt; options.AddVAppCoreInterceptors&lt;Guid, Guid&gt;(sp))</c>).
    /// </summary>
    public static DbContextOptionsBuilder AddVAppCoreInterceptors<TUserKey, TTenantKey>(
        this DbContextOptionsBuilder builder,
        IServiceProvider serviceProvider)
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        var currentUser = serviceProvider.GetService<ICurrentUser<TUserKey, TTenantKey>>();
        builder.AddInterceptors(new VAuditInterceptor<TUserKey, TTenantKey>(currentUser));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="VAuditInterceptor{TUserKey, TTenantKey}"/> with an explicitly
    /// supplied current user. Useful for tests; in production prefer the
    /// <see cref="IServiceProvider"/> overload so the user is resolved per request.
    /// </summary>
    public static DbContextOptionsBuilder AddVAppCoreInterceptors<TUserKey, TTenantKey>(
        this DbContextOptionsBuilder builder,
        ICurrentUser<TUserKey, TTenantKey>? currentUser)
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        builder.AddInterceptors(new VAuditInterceptor<TUserKey, TTenantKey>(currentUser));
        return builder;
    }

    /// <summary>
    /// Generic-options-builder overload (so call sites that have <c>DbContextOptionsBuilder&lt;T&gt;</c>
    /// don't have to upcast). Forwards to the non-generic overload above.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> AddVAppCoreInterceptors<TContext, TUserKey, TTenantKey>(
        this DbContextOptionsBuilder<TContext> builder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        AddVAppCoreInterceptors<TUserKey, TTenantKey>((DbContextOptionsBuilder)builder, serviceProvider);
        return builder;
    }

    /// <summary>
    /// Generic-options-builder overload taking an explicit <see cref="ICurrentUser{TUserKey, TTenantKey}"/>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> AddVAppCoreInterceptors<TContext, TUserKey, TTenantKey>(
        this DbContextOptionsBuilder<TContext> builder,
        ICurrentUser<TUserKey, TTenantKey>? currentUser)
        where TContext : DbContext
        where TUserKey : IEquatable<TUserKey>
        where TTenantKey : IEquatable<TTenantKey>
    {
        AddVAppCoreInterceptors<TUserKey, TTenantKey>((DbContextOptionsBuilder)builder, currentUser);
        return builder;
    }
}
