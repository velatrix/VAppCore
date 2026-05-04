using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace VAppCore;

/// <summary>
/// Replaces EF Core's default <see cref="IModelCustomizer"/> so that
/// <see cref="ModelBuilderExtensions.ApplyVAppCoreFilters{TUserKey, TTenantKey}"/>
/// runs after the consumer's <c>OnModelCreating</c> — without requiring the
/// consumer to override anything. Registered via
/// <see cref="DbContextOptionsBuilderExtensions.UseVAppCore{TContext, TUserKey, TTenantKey}"/>.
/// </summary>
public class VAppCoreModelCustomizer<TUserKey, TTenantKey> : ModelCustomizer
    where TUserKey : IEquatable<TUserKey>
    where TTenantKey : IEquatable<TTenantKey>
{
    public VAppCoreModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies) { }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.ApplyVAppCoreFilters<TUserKey, TTenantKey>(context);
    }
}
