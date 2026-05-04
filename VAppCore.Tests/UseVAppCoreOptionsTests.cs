using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VAppCore.Tests.Infrastructure;

namespace VAppCore.Tests;

/// <summary>
/// Tests the v2.0 options-based wiring: a plain DbContext with no VAppCore-specific
/// code in the class body, configured entirely through options.UseVAppCore(...).
/// Audit + soft-delete filter both wire automatically — no OnConfiguring,
/// no OnModelCreating override needed in the consumer.
/// </summary>
public class UseVAppCoreOptionsTests
{
    [Fact]
    public async Task AuditFieldsPopulate_WithoutAnyConsumerOverrides()
    {
        var (db, user) = CreateContext();

        var entity = new TestSimpleEntity { Id = Guid.NewGuid(), Name = "Test" };
        db.SimpleEntities.Add(entity);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(default, entity.CreatedAt);
        Assert.Equal(user.UserId, entity.CreatedBy);
    }

    [Fact]
    public async Task SoftDeleteFilter_AppliesWithoutAnyConsumerOverrides()
    {
        var (db, _) = CreateContext();

        var live = new TestProduct { Id = Guid.NewGuid(), Name = "Live", Price = 1m };
        var dead = new TestProduct { Id = Guid.NewGuid(), Name = "Dead", Price = 1m, IsDeleted = true, DeletedAt = DateTimeOffset.UtcNow };
        db.Products.Add(live);
        db.Products.Add(dead);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var visible = await db.Products.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(visible);
    }

    [Fact]
    public async Task ConsumerOnModelCreating_StillRuns()
    {
        var (db, _) = CreateContext();
        // PlainOptionsDbContext.OnModelCreating sets a marker on the model annotations
        var marker = db.Model.FindAnnotation("ConsumerHookRan")?.Value;
        Assert.Equal("yes", marker);
    }

    private static (PlainOptionsDbContext Db, TestCurrentUser User) CreateContext()
    {
        var user = new TestCurrentUser();
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser<Guid, Guid>>(user);
        var sp = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<PlainOptionsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseVAppCore<PlainOptionsDbContext, Guid, Guid>(sp)
            .Options;

        return (new PlainOptionsDbContext(options), user);
    }
}

// A plain DbContext with NO VAppCore-specific overrides in the class body.
// Demonstrates that v2.0 wires everything via options.
internal class PlainOptionsDbContext : DbContext
{
    public DbSet<TestProduct> Products => Set<TestProduct>();
    public DbSet<TestSimpleEntity> SimpleEntities => Set<TestSimpleEntity>();
    public DbSet<TestTenantProduct> TenantProducts => Set<TestTenantProduct>();

    public PlainOptionsDbContext(DbContextOptions options) : base(options) { }

    // Consumer's OnModelCreating runs unaltered. Marker proves it.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasAnnotation("ConsumerHookRan", "yes");
    }
}
