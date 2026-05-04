# VAppCore

Enterprise .NET 10 library for building web APIs. Provides base entities with audit fields, authentication/authorization, RSQL query parsing with field-level control, service base class, structured error handling, and response mapping enforcement.

## Table of Contents

- [Quick Start](#quick-start)
- [Entity Foundation](#entity-foundation)
- [DbContext Setup](#dbcontext-setup)
- [Authentication & Authorization](#authentication--authorization)
- [VService](#vservice)
- [Query Parsing (RSQL)](#query-parsing-rsql)
- [VQueryFilter](#vqueryfilter)
- [Domain Events + Outbox](#domain-events--outbox)
- [Audit Log](#audit-log)
- [API Key Auth](#api-key-auth)
- [Optimistic Concurrency](#optimistic-concurrency)
- [Rate Limiting](#rate-limiting)
- [Error Handling](#error-handling)
- [Response Mapping](#response-mapping)
- [Full Example](#full-example)

---

## Quick Start

### Setup

VAppCore is wired entirely at the DI / options level. Your `DbContext` class needs no VAppCore-specific code — inherit `DbContext` (or `IdentityDbContext`, or any other base) directly.

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"));
    options.UseVAppCore<AppDbContext, Guid, Guid>(sp);   // wires audit interceptor + global filters
});

builder.Services.AddControllers();

builder.Services.AddVAppCore<AppDbContext, Guid, Guid>(options =>
{
    options.UserIdClaim = "sub";
    options.TenantIdClaim = "tenant_id";
    options.RoleClaim = "role";
    options.PermissionClaim = "permission";
});

// Register your services
builder.Services.AddVServices(typeof(Program).Assembly);

var app = builder.Build();

app.UseVAppCore(); // exception handling middleware — call early
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

The two type parameters on both `UseVAppCore` and `AddVAppCore` are `TUserKey` and `TTenantKey` — the types you'll use for user and tenant ids throughout the app (typically `Guid, Guid`).

---

## Entity Foundation

### VEntity

Base class for all entities. Provides Id, audit fields (CreatedAt, UpdatedAt, CreatedBy, UpdatedBy).

```csharp
public abstract class VEntity<TKey, TUserKey, TTenantKey>
{
    public TKey Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public TUserKey CreatedBy { get; set; }
    public TUserKey UpdatedBy { get; set; }
}

// Convenience base — all Guid keys:
public abstract class VEntity : VEntity<Guid, Guid, Guid> { }
```

### Project-Level Alias

Define once in your project — never type the generic params again:

```csharp
public abstract class AppEntity : VEntity<Guid, Guid, Guid> { }
```

Then all your entities:

```csharp
public class Product : AppEntity
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}

public class Category : AppEntity
{
    public string Name { get; set; }
}
```

### ISoftDeletable

Opt-in interface. Entities implementing this are soft-deleted instead of removed — `Remove()` sets `IsDeleted = true` instead of issuing a `DELETE`.

```csharp
public class Product : AppEntity, ISoftDeletable
{
    public string Name { get; set; }
    public decimal Price { get; set; }

    // ISoftDeletable requires:
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    // Optional — add this property and VAuditInterceptor sets it automatically:
    public Guid? DeletedBy { get; set; }
}
```

Soft-deleted entities are automatically filtered from all queries via a global query filter. Use `IgnoreQueryFilters()` to see them:

```csharp
// Normal query — soft-deleted entities excluded
var products = await db.Products.ToListAsync();

// Include soft-deleted
var all = await db.Products.IgnoreQueryFilters().ToListAsync();
```

### ITenantScoped

Opt-in interface for multi-tenancy. With VAppCore wired:
- `VAuditInterceptor` sets `TenantId` from the current user on new entities (when not already set)
- `ApplyVAppCoreFilters` adds a global query filter that scopes queries to the current tenant — but only when your DbContext implements `IVTenantContext<TTenantKey>` (see DbContext Setup below)

```csharp
public class Product : AppEntity, ITenantScoped<Guid>
{
    public string Name { get; set; }
    public Guid TenantId { get; set; }
}
```

An entity can implement both:

```csharp
public class Product : AppEntity, ISoftDeletable, ITenantScoped<Guid>
{
    public string Name { get; set; }

    // ISoftDeletable
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }

    // ITenantScoped
    public Guid TenantId { get; set; }
}
```

---

## DbContext Setup

### Plain DbContext

Your `DbContext` class is plain — no VAppCore base class to inherit:

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
}
```

All the wiring happens at registration time:

```csharp
services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
    options.UseVAppCore<AppDbContext, Guid, Guid>(sp);
});
```

`UseVAppCore` does two things:
1. Adds `VAuditInterceptor` (handles audit fields, soft delete, tenant assignment on SaveChanges)
2. Replaces `IModelCustomizer` with one that calls `ApplyVAppCoreFilters` after your `OnModelCreating` runs (handles soft-delete and tenant global query filters)

### Works with any DbContext base

Because the wiring is at the options level, your context can inherit anything — `DbContext`, `IdentityDbContext`, a Cosmos base, your own custom base. The setup is identical:

```csharp
// ASP.NET Identity case
public class ApplicationDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }
}

services.AddDbContext<ApplicationDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
    options.UseVAppCore<ApplicationDbContext, Guid, Guid>(sp);
});
```

Identity keeps owning the AspNet* tables and their existing audit fields. VAppCore audits, soft-deletes, and tenant-scopes everything else.

### Multi-tenancy: implement IVTenantContext

Tenant scoping is opt-in. If your app is multi-tenant, implement `IVTenantContext<TTenantKey>` on your DbContext so the global query filter can read the current tenant:

```csharp
public class AppDbContext : DbContext, IVTenantContext<Guid>
{
    private readonly ICurrentUser<Guid, Guid>? _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider sp) : base(options)
    {
        _currentUser = sp.GetService<ICurrentUser<Guid, Guid>>();
    }

    public Guid CurrentTenantId =>
        _currentUser is { IsAuthenticated: true } ? _currentUser.TenantId : default;
}
```

If you don't implement `IVTenantContext`, `ITenantScoped<T>` entities still get their `TenantId` auto-assigned on Add (when authenticated), but no global query filter is applied.

### What the interceptor does on SaveChanges

**Added entities:**
- Sets `CreatedAt`, `UpdatedAt` to current UTC time
- Sets `CreatedBy`, `UpdatedBy` from current user
- Sets `TenantId` from current user (if `ITenantScoped`)

**Modified entities:**
- Updates `UpdatedAt` to current UTC time
- Updates `UpdatedBy` from current user
- Prevents `CreatedAt` and `CreatedBy` from being overwritten

**Deleted entities (ISoftDeletable):**
- Converts `DELETE` to `UPDATE` — sets `IsDeleted = true`, `DeletedAt`, `DeletedBy`
- Entity stays in the database, filtered from normal queries

**Deleted entities (not ISoftDeletable):**
- Normal `DELETE` — row removed from database

> **Tenant assignment respects explicit values.** `TenantId` is only auto-assigned on Add when the entity's `TenantId` is currently default. Admin tools or seed code that explicitly set `TenantId` are respected.

### TransactionAsync

Wraps an operation in a transaction. If already inside a transaction, reuses it (no nesting issues).

```csharp
// Two services, one transaction — if either fails, both roll back
public class OrderService : AppService<Order>
{
    private readonly StockService _stock;

    public async Task<Order> PlaceOrder(CreateOrderDto dto)
    {
        return await Db.TransactionAsync(async () =>
        {
            var order = new Order { ProductId = dto.ProductId, Quantity = dto.Quantity };
            Set.Add(order);
            await SaveAsync();

            // StockService.Reduce calls SaveAsync too — same transaction
            await _stock.Reduce(dto.ProductId, dto.Quantity);

            return order;
        });
    }
}
```

`StockService.Reduce` works normally when called alone, and participates in the transaction when called from `PlaceOrder` — no changes needed in `StockService`.

---

## Authentication & Authorization

### ICurrentUser

Represents the authenticated user. Available in services via `CurrentUser` property.

```csharp
public interface ICurrentUser<TUserKey, TTenantKey>
{
    TUserKey UserId { get; }
    TTenantKey TenantId { get; }
    string? Email { get; }
    IReadOnlyList<string> Roles { get; }
    IReadOnlyList<string> Permissions { get; }
    bool IsAuthenticated { get; }

    /// <summary>
    /// Auth scheme name from the underlying ClaimsPrincipal (e.g. "Cookies", "ApiKey").
    /// Null for unauthenticated callers.
    /// </summary>
    string? AuthenticationType { get; }

    bool IsInRole(string role);
    bool HasPermission(string permission);
}
```

### ClaimsCurrentUser (Default)

The library provides `ClaimsCurrentUser` — reads from `ClaimsPrincipal`, works with any auth mechanism (JWT, Cookie, OpenID Connect, ASP.NET Identity).

Configured via `AddVAppCore`:

```csharp
builder.Services.AddVAppCore<AppDbContext, Guid, Guid>(options =>
{
    options.UserIdClaim = "sub";           // default
    options.TenantIdClaim = "tenant_id";   // default
    options.RoleClaim = ClaimTypes.Role;    // default
    options.PermissionClaim = "permission"; // default
    options.EmailClaim = "email";           // default
});
```

### UseAspNetIdentity preset

When the auth layer is ASP.NET Identity (cookies issued by `SignInManager`), claims are emitted under `ClaimTypes.NameIdentifier` / `ClaimTypes.Role` / `ClaimTypes.Email` rather than the OIDC defaults. Use the preset to switch all three at once:

```csharp
builder.Services.AddVAppCore<ApplicationDbContext, Guid, Guid>(o => o.UseAspNetIdentity());
```

The preset only mutates options — it adds **no package dependency** on Identity. `ClaimTypes` is in `System.Security.Claims` (the BCL).

### IPermissionResolver (Database-based)

If your roles/permissions live in the database instead of JWT claims, implement `IPermissionResolver`:

```csharp
public class DbPermissionResolver : IPermissionResolver<Guid>
{
    private readonly AppDbContext _db;
    public DbPermissionResolver(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId)
    {
        return await _db.UserRoles
            .Where(x => x.UserId == userId)
            .Select(x => x.Role.Name)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId)
    {
        return await _db.RolePermissions
            .Where(x => _db.UserRoles
                .Where(ur => ur.UserId == userId)
                .Select(ur => ur.RoleId)
                .Contains(x.RoleId))
            .Select(x => x.Permission)
            .Distinct()
            .ToListAsync();
    }
}

// Register:
builder.Services.AddScoped<IPermissionResolver<Guid>, DbPermissionResolver>();
```

When registered, `ClaimsCurrentUser` uses the resolver instead of reading from claims. Results are cached per request.

### VAuthorize Attribute

Apply to controllers or actions. Stackable — multiple attributes require ALL conditions.

```csharp
// Entire controller requires authentication
[VAuthorize]
[ApiController]
[Route("api/products")]
public class ProductController(ProductService products) : ControllerBase
{
    // Just needs to be authenticated (inherited from class)
    [HttpGet]
    public async Task<IActionResult> GetAll(VQueryParser parser) { }

    // Needs specific permission
    [HttpPost]
    [VAuthorize(Permission = "products.create")]
    public async Task<IActionResult> Create(CreateProductDto dto) { }

    // Needs specific role
    [HttpDelete("{id}")]
    [VAuthorize(Role = "Admin")]
    public async Task<IActionResult> Delete(Guid id) { }

    // Needs both role AND permission
    [HttpPut("{id}")]
    [VAuthorize(Role = "Admin")]
    [VAuthorize(Permission = "products.update")]
    public async Task<IActionResult> Update(Guid id, UpdateProductDto dto) { }
}

// No auth — just don't add the attribute
[ApiController]
[Route("api/public")]
public class PublicController : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health() => Ok("alive");
}
```

- `[VAuthorize]` alone = must be authenticated
- `[VAuthorize(Permission = "...")]` = needs specific permission
- `[VAuthorize(Role = "...")]` = needs specific role
- Multiple `[VAuthorize]` = must satisfy ALL
- No attribute = public endpoint

Unauthorized returns `401`, forbidden returns `403` — both in the standard `ErrorContext` format.

---

## VService

### Setup

Base service with auto-injected `Db`, `CurrentUser`, and `Set`. No constructor boilerplate.

```csharp
// Project-level alias:
public abstract class AppService<T> : VService<T, Guid, Guid, Guid>
    where T : AppEntity { }

// Simple service — no extra dependencies:
public class CategoryService : AppService<Category>
{
    public async Task<Category> Create(CreateCategoryDto dto)
    {
        var category = new Category { Name = dto.Name };
        Set.Add(category);
        await SaveAsync();
        return category;
    }
}

// Service with extra dependencies — only add what YOU need:
public class OrderService(IEmailSender email) : AppService<Order>
{
    public async Task<Order> Create(CreateOrderDto dto)
    {
        var order = new Order { ProductId = dto.ProductId };
        Set.Add(order);
        await SaveAsync();

        await email.SendAsync(CurrentUser.Email, "Order placed!");
        return order;
    }
}
```

### Registration

```csharp
// One at a time:
builder.Services.AddVService<ProductService>();

// Or scan entire assembly:
builder.Services.AddVServices(typeof(Program).Assembly);
```

### Built-in Methods

**GetByIdAsync** — throws `NotFoundError` (404) if not found:

```csharp
// Simple:
var product = await GetByIdAsync(id);

// With includes/extra conditions:
var product = await GetByIdAsync(id, q => q
    .Include(p => p.Category)
    .Include(p => p.Orders));
```

**FindByIdAsync** — returns `null` if not found:

```csharp
var existing = await FindByIdAsync(id);
if (existing is not null)
    throw new ConflictError(new ErrorObject { Message = "Already exists" });
```

**GetPagedAsync** — uses VQueryParser for filtering, sorting, pagination:

```csharp
// Default — full table:
var result = await GetPagedAsync(parser);

// With pre-filtering:
var result = await GetPagedAsync(parser, q => q
    .Where(p => p.CategoryId == categoryId));

// Scoped to current user:
var result = await GetPagedAsync(parser, q => q
    .Where(o => o.CreatedBy == CurrentUser.UserId));
```

**DeleteAsync** — soft or hard depending on entity:

```csharp
await DeleteAsync(id); // 404 if not found
```

Override for custom logic:

```csharp
public override async Task DeleteAsync(Guid id)
{
    var product = await GetByIdAsync(id, q => q.Include(p => p.Orders));

    if (product.Orders.Any())
        throw new BadRequestError(new ErrorObject
        {
            Message = "Cannot delete product with orders",
            MessageKey = "products.errors.hasOrders"
        });

    Set.Remove(product);
    await SaveAsync();
}
```

**SaveAsync** — shortcut for `Db.SaveChangesAsync()`.

---

## Query Parsing (RSQL)

VQueryParser reads `filter`, `sort`, `select`, `cursor` / `before`, `page`, and `limit` from query parameters and applies them to an `IQueryable`.

### Query Parameters

| Parameter | Example | Description |
|-----------|---------|-------------|
| `filter` | `name==John;age=gt=25` | RSQL filter expression |
| `sort` | `-createdAt,+name` | `+` ascending, `-` descending |
| `select` | `id,name,email` | Fields to include in response |
| `limit` | `20` | Rows per page (default: 20, max: 100) |
| `cursor` | `eyJzIjoiK25hbWUiLCJ2IjpbXX0=` | Forward cursor — returns rows after this position |
| `before` | `eyJzIjoiK25hbWUiLCJ2IjpbXX0=` | Backward cursor — returns rows before this position |
| `page` | `2` | Page number (1-based) — only on filters that opt in via `EnablePageNavigation()` |

### RSQL Operators

| Operator | Syntax | Example |
|----------|--------|---------|
| Equal | `==` | `name==John` |
| Not equal | `!=` | `status!=Active` |
| Greater than | `=gt=` | `age=gt=25` |
| Greater or equal | `=ge=` | `price=ge=100` |
| Less than | `=lt=` | `age=lt=30` |
| Less or equal | `=le=` | `price=le=50` |
| In | `=in=` | `status=in=(Active,Pending)` |
| Not in | `=out=` | `status=out=(Deleted)` |
| Like | `=like=` | `name=like=*John*` |
| Case-insensitive like | `=ilike=` | `name=ilike=*john*` |
| Is null | `=isnull=` | `deletedAt=isnull=true` |
| Is not null | `=isnotnull=` | `email=isnotnull=true` |

### Logical Operators

- **AND**: `;` — `name==John;age=gt=25`
- **OR**: `,` — `name==John,name==Jane`
- **Parentheses**: `(name==John,name==Jane);age=gt=25`

AND binds tighter than OR. Parentheses override precedence.

### Like Patterns

- `*value*` → Contains
- `value*` → StartsWith
- `*value` → EndsWith
- `*val*ue*` → Regex (complex patterns)

### Supported Types

String, int, long, double, decimal, bool, DateTime, DateTimeOffset, Guid, enums. Values are auto-converted based on the entity property type.

### Quoted Values

Use single or double quotes for values with spaces or special characters:

```
name=='John Doe'
name=="O'Brien"
name=='it\'s escaped'
```

### Applying to IQueryable

```csharp
// Filter + sort + offset pagination (no field selection):
IQueryable<T> result = parser.Apply(queryable);

// Filter + sort + offset pagination + total count:
var (items, totalCount) = await parser.ApplyWithCountAsync<T>(queryable);

// Filter + sort + offset pagination + field projection:
VPagedResponse<object> result = await parser.ApplyWithProjectionAsync<T>(queryable);

// Cursor pagination (forward via ?cursor=X, backward via ?before=X):
VPagedResponse<T> result = await parser.ApplyWithCursorAsync<T>(queryable, codec);
VPagedResponse<object> result = await parser.ApplyWithCursorProjectionAsync<T>(queryable, codec);

// Individual operations:
queryable = parser.ApplyFilter(queryable);
queryable = parser.ApplySort(queryable);
queryable = parser.ApplyPagination(queryable);
```

### Cursor pagination

`VService.GetPagedAsync(parser)` is the unified entry — it picks the mode based on the request:

- `?cursor=X` (or no pagination params) → cursor mode (fast keyset query, no COUNT)
- `?before=X` → backward cursor mode (returns rows in display order, before the cursor)
- `?page=N` → offset mode — only if the filter opted in via `EnablePageNavigation()`, otherwise 400

Cursor encodes the request's sort fields plus the entity Id as a stable tiebreaker, so paging is correct under concurrent inserts and constant-time at any depth. Limit can change between requests freely. If sort changes, the cursor is silently discarded and the response is page 1 of the new sort — detect via `previousCursor === null`.

**Response shape — single `VPagedResponse<T>` for both modes:**

```json
{
  "items": [...],
  "limit": 50,
  "hasMore": true,
  "nextCursor": "...",          // null when no more rows
  "previousCursor": "...",      // null on the first page
  "page": 2,                    // populated only in offset mode
  "totalItems": 5000,           // populated only in offset mode (COUNT runs)
  "totalPages": 100             // computed from totalItems / limit
}
```

**Opting a filter into page-navigation:**

```csharp
public class UserAdminFilter : VQueryFilter<User>
{
    public UserAdminFilter()
    {
        AllowAll();
        EnablePageNavigation();   // ← lets the endpoint accept ?page=N
    }
}
```

**Cursor encryption (optional, with key rotation):**

By default, cursors are unencrypted base64-of-JSON — opaque to clients but tamperable. Configure one or more 32-byte (256-bit) AES-GCM keys to make them tamper-proof and unreadable:

```csharp
// Single key — simplest setup
services.AddVAppCore<AppDb, Guid, Guid>(o =>
{
    o.CursorEncryptionKeys = [builder.Configuration["VAppCore:CursorKey"]!];
});

// Key rotation — encrypt with the first key, decrypt by trying each in order
services.AddVAppCore<AppDb, Guid, Guid>(o =>
{
    o.CursorEncryptionKeys = [
        builder.Configuration["VAppCore:CursorKey"]!,        // current
        builder.Configuration["VAppCore:CursorKey:Previous"]! // accepted during transition window
    ];
});
```

Rotation flow: deploy with `[newKey, oldKey]` so existing cursors keep decrypting. After enough time has passed that all in-flight cursors are gone (typical: 1 day), redeploy with `[newKey]` only.

For KMS / Azure Key Vault, register a custom `ICursorProtector` in DI before calling `AddVAppCore` — it wins over the built-in `AesGcmCursorProtector`.

**NULL handling in cursor sorts:**

Sort fields with NULL values are handled correctly: NULLs always sort LAST regardless of `+` (asc) or `-` (desc) direction. Cursor positioned in the non-null section continues normally and crosses into the NULL section as expected. Cursor positioned at a NULL value paginates within the NULL section (ordered by id tiebreaker).

**`CustomField` as cursor sort:**

Sorting by a `CustomField` (via `WithExpression`, `CountOf`, `FromNavigation`, `WithNullCheck`) is rejected with HTTP 400 in cursor mode — the computed expression can't be reliably reproduced in the cursor's WHERE clause. Use offset pagination (`?page=N` on a filter with `EnablePageNavigation()`) when sorting by computed fields, or sort by a real entity property.

**Fundamental cursor-pagination limitation:**

If a row's sort-field value changes between cursor requests (e.g., a user's score updates while you're paginating a leaderboard), that row may be skipped or appear twice. This is true of every cursor pagination implementation — mitigations require snapshot tables or serializable transactions.

### Standalone RSQL Extension

Apply RSQL filtering without VQueryParser:

```csharp
var filtered = db.Products.ApplyRsql("price=gt=100;status==Active");
```

### Model Binding

VQueryParser is automatically bound from query parameters in controllers:

```csharp
[HttpGet]
[UseVQueryParser(typeof(ProductFilter))]
public async Task<IActionResult> GetAll(VQueryParser parser)
{
    return Ok(await products.GetPagedAsync(parser));
}
```

---

## VQueryFilter

VQueryFilter defines which fields are allowed for filtering, sorting, and selection on an entity. It acts as a whitelist — fields not configured are rejected.

### Basic Field Registration

```csharp
public class ProductFilter : VQueryFilter<Product>
{
    public ProductFilter()
    {
        Field(x => x.Id).Filterable().Sortable().Selectable();
        Field(x => x.Name).Filterable().Sortable().Selectable();
        Field(x => x.Price).Filterable().Sortable().Selectable();
        Field(x => x.CreatedAt).Filterable().Sortable();
        Field(x => x.Status).Filterable();
    }
}
```

Each method is independent — a field can be filterable but not sortable, or selectable but not filterable:

```csharp
Field(x => x.Email).Filterable().Selectable();     // can filter and select, but NOT sort
Field(x => x.InternalScore).Filterable();           // can filter, but NOT select or sort
Field(x => x.DisplayName).Selectable();             // can select, but NOT filter or sort
```

### Filterable

Allows the field to be used in RSQL `filter` expressions:

```csharp
Field(x => x.Name).Filterable();
// GET /products?filter=name==iPhone    ← allowed
// GET /products?filter=secret==123     ← rejected (not configured)
```

### Sortable

Allows the field to be used in `sort` parameter:

```csharp
Field(x => x.Price).Sortable();
// GET /products?sort=-price     ← allowed
// GET /products?sort=+secret    ← rejected
```

### Selectable

Allows the field to be included in `select` parameter and returned in projections:

```csharp
Field(x => x.Name).Selectable();
// GET /products?select=name     ← allowed
// GET /products?select=secret   ← rejected
```

When no `select` parameter is provided and `DefaultSelect` is not set, all selectable fields are returned.

### Chaining

All methods return the config, so you can chain:

```csharp
Field(x => x.Name).Filterable().Sortable().Selectable();
```

### Field Aliases

Map an alias to a real property. The alias inherits the same capabilities:

```csharp
Field(x => x.Name).Filterable().Sortable().Selectable().WithAlias("username");

// Both work:
// GET /products?filter=name==John
// GET /products?filter=username==John     ← resolves to Name
// GET /products?sort=+username            ← resolves to Name
```

### Nested Fields

Access properties through navigation:

```csharp
Field(x => x.Address.City).Filterable().Selectable();
Field(x => x.Category.Name).Filterable().Sortable().Selectable();

// GET /products?filter=address.city==London
// GET /products?select=category.name
```

VQueryParser automatically detects nested fields and calls `.Include()` for the navigation property.

### Collection Fields

Access properties within collection navigations:

```csharp
CollectionField(x => x.Orders, o => o.Amount).Selectable();
CollectionField(x => x.Tags, t => t.Name).Filterable().Selectable();

// GET /products?select=orders.amount,tags.name
```

### AllowAll

Shortcut — allows all public properties for filtering, sorting, and selection:

```csharp
public class ProductFilter : VQueryFilter<Product>
{
    public ProductFilter()
    {
        AllowAll();
    }
}
```

Use with caution — exposes all fields.

### Default Sort

Applied when no `sort` parameter is provided:

```csharp
SetDefaultSort("-createdAt");            // newest first
SetDefaultSort("-createdAt,+name");      // newest first, then by name
```

Format: `+field` ascending, `-field` descending. Multiple fields separated by commas.

### Default Select

Fields returned when no `select` parameter is provided:

```csharp
SetDefaultSelect("id", "name", "email", "createdAt");
```

If not set, all selectable fields are returned.

### Custom Fields — Navigation Projection

Project fields from a nullable navigation property. Returns the nested object when it exists, null otherwise:

```csharp
CustomField("author")
    .FromNavigation("User")
    .SubField("Id", "id")
    .SubField("Username", "username")
    .SubField("Email", "email")
    .Selectable();

// GET /products?select=id,name,author
// Response:
// {
//   "id": "...",
//   "name": "Product",
//   "author": { "id": "...", "username": "john", "email": "john@example.com" }
//   // or "author": null if no User navigation
// }
```

### Custom Fields — Count

Return the count of a collection navigation:

```csharp
CustomField("orderCount")
    .CountOf("Orders")
    .Selectable()
    .Sortable();

// GET /products?select=id,name,orderCount&sort=-orderCount
// Response:
// { "id": "...", "name": "Product", "orderCount": 42 }
```

### Custom Fields — Raw Expression

Use a raw System.Linq.Dynamic.Core expression:

```csharp
CustomField("fullName")
    .WithExpression("it.FirstName + \" \" + it.LastName")
    .Selectable();

// GET /users?select=id,fullName
// Response:
// { "id": "...", "fullName": "John Doe" }
```

### Custom Fields — Null Check (Boolean)

Expose a boolean field that checks if a navigation property is null or not. Useful for "has relationship" filtering:

```csharp
CustomField("isAutomated")
    .WithNullCheck("Flow")
    .Filterable()
    .Selectable();

// GET /tasks?filter=isAutomated==true     → WHERE Flow IS NOT NULL
// GET /tasks?filter=isAutomated==false    → WHERE Flow IS NULL
// GET /tasks?select=id,name,isAutomated
// Response:
// { "id": "...", "name": "Task", "isAutomated": true }
```

### Validation Behavior

When a VQueryFilter is attached (via `[UseVQueryParser]` attribute), all fields are validated:

```csharp
// filter fields validated:
// GET /products?filter=nonExistent==value
// → 422: "Field(s) not allowed for filtering: nonExistent. Allowed fields: id, name, price"

// sort fields validated:
// GET /products?sort=+email
// → 422: "Field(s) not allowed for sorting: email. Allowed sort fields: id, name, price"

// select fields validated:
// GET /products?select=secret
// → 422: "Field(s) not allowed for selection: secret. Allowed select fields: id, name, price"
```

Without a VQueryFilter, no validation — all fields accepted.

### Complete Example

```csharp
public class ProductFilter : VQueryFilter<Product>
{
    public ProductFilter()
    {
        // Basic fields
        Field(x => x.Id).Filterable().Sortable().Selectable();
        Field(x => x.Name).Filterable().Sortable().Selectable().WithAlias("title");
        Field(x => x.Price).Filterable().Sortable().Selectable();
        Field(x => x.Status).Filterable().Selectable();
        Field(x => x.CreatedAt).Filterable().Sortable().Selectable();

        // Nested field
        Field(x => x.Category.Name).Filterable().Sortable().Selectable();

        // Collection field
        CollectionField(x => x.Tags, t => t.Name).Filterable().Selectable();

        // Custom: navigation projection
        CustomField("creator")
            .FromNavigation("CreatedByUser")
            .SubField("Id", "id")
            .SubField("Username", "username")
            .Selectable();

        // Custom: count
        CustomField("orderCount").CountOf("Orders").Selectable().Sortable();

        // Custom: null check
        CustomField("hasDiscount").WithNullCheck("Discount").Filterable().Selectable();

        // Defaults
        SetDefaultSort("-createdAt");
        SetDefaultSelect("id", "name", "price", "status", "createdAt");
    }
}
```

### Using with Controller

```csharp
[ApiController]
[Route("api/products")]
public class ProductController(ProductService products) : ControllerBase
{
    [HttpGet]
    [UseVQueryParser(typeof(ProductFilter))]
    public async Task<IActionResult> GetAll(VQueryParser parser)
    {
        return Ok(await products.GetPagedAsync(parser));
    }
}
```

Or with the generic attribute:

```csharp
[HttpGet]
[UseVQueryParser<ProductFilter>]
public async Task<IActionResult> GetAll(VQueryParser parser)
{
    return Ok(await products.GetPagedAsync(parser));
}
```

### Example Queries

```
# Filter by name and price
GET /products?filter=name=like=*Phone*;price=le=1000

# Filter with OR
GET /products?filter=status==Active,status==Pending

# Combined with parentheses
GET /products?filter=(status==Active,status==Pending);price=gt=50

# In operator
GET /products?filter=status=in=(Active,Pending,Review)

# Null check
GET /products?filter=deletedAt=isnull=true

# Sort descending by price, then ascending by name
GET /products?sort=-price,+name

# Select specific fields
GET /products?select=id,name,price,category.name,orderCount

# Cursor pagination (default — first page)
GET /products?limit=25

# Cursor pagination — next page
GET /products?cursor=eyJzIjoiK25hbWUiLCJ2IjpbXX0=&limit=25

# Cursor pagination — previous page
GET /products?before=eyJzIjoiK25hbWUiLCJ2IjpbXX0=&limit=25

# Offset pagination (filter must opt-in via EnablePageNavigation)
GET /products?page=2&limit=25

# Everything combined (cursor mode)
GET /products?filter=price=gt=100;status==Active&sort=-createdAt&select=id,name,price&limit=10
```

Response format for `ApplyWithProjectionAsync`:

```json
{
  "items": [
    { "id": "...", "name": "iPhone", "price": 999 },
    { "id": "...", "name": "MacBook", "price": 1999 }
  ],
  "limit": 10,
  "page": 1,
  "totalItems": 42,
  "totalPages": 5,
  "nextCursor": "...",
  "previousCursor": null,
  "hasMore": true
}
```

---

## Domain Events + Outbox

When something happens in the domain — a user registers, a match completes, a friendship is accepted — you often want multiple things to react: send an email, update analytics, notify friends, recompute a leaderboard. Inlining all reactions into the service that triggered the change couples that service to every consumer and offers no reliability guarantees if any consumer fails. Wrapping in a database transaction can't help — external side effects (HTTP calls, emails, push notifications) cannot participate in a database transaction.

VAppCore ships a transactional outbox: raise events on entities, the interceptor persists them to an `OutboxMessages` table in the same transaction as the entity changes, and a background processor delivers them to handlers with retry, dead-letter, and pruning.

### Define an event and a handler

```csharp
public record UserRegistered(Guid UserId, string Email) : IDomainEvent;

public class SendWelcomeEmail : IDomainEventHandler<UserRegistered>
{
    private readonly IEmailService _email;
    public SendWelcomeEmail(IEmailService email) => _email = email;

    public Task Handle(UserRegistered evt, EventContext ctx, CancellationToken ct)
        => _email.SendWelcomeAsync(evt.Email);
}
```

### Raise the event in your service

```csharp
public class UserService : AppService<User>
{
    public async Task<User> Register(RegisterDto dto)
    {
        var user = new User { Email = dto.Email };
        Set.Add(user);
        user.RaiseEvent(new UserRegistered(user.Id, user.Email));
        await Db.SaveChangesAsync();
        return user;
    }
}
```

`RaiseEvent` only adds the event to an in-memory list on the entity. The `OutboxInterceptor` runs during `SaveChanges`, walks the change tracker, finds entities with pending events, and inserts an `OutboxMessage` row for each — atomically with the entity change. Either both commit (user + outbox row) or neither does.

### Wire it up

```csharp
// Program.cs
services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(connStr);
    options.UseVAppCore<AppDbContext, Guid, Guid>(sp);
    options.AddInterceptors(sp.GetRequiredService<OutboxInterceptor>());
});

services.AddVAppCore<AppDbContext, Guid, Guid>();
services.AddVAppCoreOutbox<AppDbContext>(o =>
{
    o.PollInterval = TimeSpan.FromSeconds(2);
    o.MaxAttempts = 10;
    o.MaxBackoff = TimeSpan.FromMinutes(5);
    o.RetentionDays = 30;
});
services.AddDomainEventHandlers(typeof(Program).Assembly);
```

Add `DbSet<OutboxMessage>` to your `DbContext` and create an EF migration to materialize the table.

### What happens at runtime

- `Register` returns when the database commit completes — fast, ~5ms. The user gets `202 Accepted`.
- A background `OutboxProcessor` polls `OutboxMessages WHERE Status = 'Pending'` every `PollInterval`.
- For each row, it deserializes the payload, resolves all `IDomainEventHandler<T>` instances from DI, and invokes each. On success, the row is marked `Sent` (and pruned later). On failure, the row stays `Pending` with `NextRetryAt` set per exponential backoff.
- After `MaxAttempts` failures, the row moves to `DeadLettered` for manual review.

### Handler idempotency

The outbox guarantees at-least-once delivery, not exactly-once. If your process crashes after a handler completes its work but before the row is marked `Sent`, the next poll will re-dispatch and the handler will run again. **Handlers must be idempotent.**

The `EventContext` parameter carries the unique `MessageId` of the outbox row — handlers can use it as an idempotency key (e.g., write to a `ProcessedEvents` table; check before doing the work):

```csharp
public Task Handle(UserRegistered evt, EventContext ctx, CancellationToken ct)
{
    // ctx.MessageId is the unique outbox row id.
    // ctx.Attempt is the 1-based retry count.
    // Use these for idempotency or "log only on first attempt" logic.
    return DoTheWork(evt);
}
```

### Cross-aggregate writes — keep them in their own services

If your registration needs to also create a `PlayerProfile` and a `Friendship`, don't reach into those tables directly from `UserService`. Either:

**Pattern A — eventual:** Each is its own handler.
```csharp
public class CreateDefaultProfile : IDomainEventHandler<UserRegistered>
{
    private readonly PlayerProfileService _profiles;
    public CreateDefaultProfile(PlayerProfileService profiles) => _profiles = profiles;
    public Task Handle(UserRegistered evt, EventContext ctx, CancellationToken ct)
        => _profiles.Create(evt.UserId);
}
```

**Pattern B — synchronous, in-transaction:** Call the owner services from `UserService` inside `Db.TransactionAsync(...)`, raise the event for the *external* side effects (email, analytics) only.

Pick A when the consumer can lag (notifications, analytics). Pick B when the related state must be visible immediately (profile must exist for the API to serve the user).

### Configuration

| Option | Default | Description |
|---|---|---|
| `PollInterval` | 2s | How often the processor checks for Pending rows |
| `BatchSize` | 50 | Max rows fetched per poll |
| `MaxAttempts` | 10 | Failures before a row is dead-lettered |
| `MaxBackoff` | 5min | Cap on exponential retry delay (`min(2^attempts, MaxBackoff)`) |
| `PruneInterval` | 1h | How often Sent rows older than retention are deleted |
| `RetentionDays` | 30 | Sent rows older than this are deleted by the prune pass |

---

## Audit Log

Opt-in per-entity history with field-level JSONB diffs, written in the same transaction as the change. Use it for moderation, fraud investigation, GDPR exports — wherever you need to answer "who changed what, when, and from what to what."

### Setup

```csharp
// Program.cs
services.AddVAppCoreAuditLog<HubDbContext, Guid, Guid>();

services.AddDbContext<HubDbContext>((sp, opts) =>
{
    opts.UseNpgsql(connStr);
    opts.UseVAppCore<HubDbContext, Guid, Guid>(sp);
    opts.AddVAppCoreAuditInterceptors<HubDbContext, Guid, Guid>(sp);
});
```

> **Order matters:** `UseVAppCore` must appear before `AddVAppCoreAuditInterceptors`. The audit interceptor reads entry state that `VAuditInterceptor` has already transformed (e.g. `ISoftDeletable` Deleted → Modified + IsDeleted=true). Swap the order and soft deletes record as `Action=Modify` with an `isDeleted` flip instead of `Action=Delete`.

Add `DbSet<AuditLog>` to your DbContext and create an EF migration. The interceptor throws a clear `InvalidOperationException` on the first save if you forget the DbSet.

### Mark entities to track

```csharp
public class Lobby : VEntity<Guid, Guid, Guid>, IAuditedEntity
{
    public string Name { get; set; } = null!;
    public int MaxPlayers { get; set; }

    [NotAudited]
    public DateTimeOffset LastSeenAt { get; set; } // excluded from diffs
}
```

Entities without `IAuditedEntity` are completely ignored — zero overhead.

### Query history

```csharp
public class LobbiesController(IAuditLog audit) : ControllerBase
{
    [HttpGet("{id}/history")]
    public Task<IReadOnlyList<AuditLog>> History(Guid id)
        => audit.GetHistoryAsync<Lobby>(id);
}
```

Returns rows newest-first, indexed on `(EntityType, EntityId)`.

### Suppress for bulk imports

```csharp
using (audit.Suppress())
{
    db.Lobbies.AddRange(thousandsOfLobbies);
    await db.SaveChangesAsync();
}
// no audit rows written; nested scopes use a depth counter
```

### Diff JSON shape

```jsonc
// Modify
{ "name": { "old": "Old", "new": "New" }, "maxPlayers": { "old": 4, "new": 8 } }

// Add
{ "name": { "old": null, "new": "Initial" } }

// Delete (hard or soft)
{ "name": { "old": "Final", "new": null } }
```

### Default-skipped fields

The diff never includes audit/concurrency/soft-delete metadata: `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`, `RowVersion`, `Xmin`, `IsDeleted`, `DeletedAt`, `DeletedBy`. Soft-deletes are recorded with `Action = Delete` (not as an `IsDeleted` field flip). Mark additional fields with `[NotAudited]` to keep noisy fields out of the diff.

### Caveats

- **Owned entities** (`OwnsOne` / `OwnsMany`) are not included in the parent entity's diff. EF tracks them as separate change-tracker entries. To audit changes inside an owned type, mark the owned class itself with `IAuditedEntity`.
- **Lazy-loading proxies**: if you enable `UseLazyLoadingProxies()`, `EntityType` is recorded as the proxy type name (e.g. `LobbyProxy`) rather than the entity type name. `GetHistoryAsync<Lobby>(id)` will not find rows written via the proxy. Either avoid lazy-loading proxies on `IAuditedEntity` types or query by raw entity-type name.

---

## API Key Auth

Service-to-service authentication via `X-Api-Key` header. Issue scoped, revocable, expiring keys to machine callers (game servers, bots, integrations) without minting user JWTs.

### Setup

```csharp
// Program.cs
services.AddVApiKeyAuth<HubDbContext>();    // registers IApiKeyService

services.AddAuthentication()
    .AddCookie()                                // existing user auth
    .AddVApiKey();                              // new scheme: reads X-Api-Key
```

Add `DbSet<ApiKey>` to your DbContext and create an EF migration. `ApiKey` implements `IAuditedEntity` — pair with v1.7 audit log to record every create/revoke/rotate.

### Create a key (admin endpoint)

```csharp
[HttpPost("/api/admin/api-keys")]
[VAuthorize(Permission = "admin.api-keys.manage")]   // user permission
public async Task<CreateApiKeyResponse> Create(IApiKeyService keys, CreateApiKeyRequest req)
{
    var (key, plaintext) = await keys.CreateAsync(
        name: req.Name,                             // "core-game-server-prod"
        permissions: req.Permissions,               // ["matches.report", "matches.read"]
        expiresAt: DateTime.UtcNow.AddYears(1));

    return new CreateApiKeyResponse
    {
        Id = key.Id,
        Name = key.Name,
        Prefix = key.Prefix,
        Secret = plaintext        // shown ONCE — caller must save now
    };
}
```

### Restrict an endpoint to API key callers

```csharp
[HttpPost("/api/matches")]
[VAuthorize(ApiKey = "matches.report")]   // user cookies are REJECTED here
public Task<IActionResult> Report(MatchResultDto dto) => ...;
```

### Revoke / rotate

```csharp
await keys.RevokeAsync(keyId);                         // next request → 401
var (newKey, newSecret) = await keys.RotateAsync(id);  // revoke old, create new with same name+permissions
```

### Failure modes

| Situation | Response |
|---|---|
| Missing `X-Api-Key` on `[VAuthorize(ApiKey=...)]` endpoint | 401 `server.errors.unauthenticated` |
| Unknown / revoked / expired key | 401 (uniform `Authentication failed` — no distinction by design, prevents enumeration via timing) |
| User cookie on `[VAuthorize(ApiKey=...)]` endpoint | 403 `api_key.required` |
| ApiKey valid but missing permission | 403 `permission.required` |

### Notes

- Plaintext format: `vk_live_<43 base64-url chars>` (32 random bytes / 256 bits entropy).
- Storage: SHA-256 hex of the full plaintext. The plaintext is shown ONCE on create/rotate and never persisted. The `Prefix` field stores the first 12 chars (e.g. `vk_live_a1b2`) for admin-UI display only.
- Lookup: `WHERE HashedSecret = @hash` — single indexed query per request.
- `LastUsedAt` is updated fire-and-forget after successful authentication; failures here never block the request.
- `IApiKeyService` is usable without the auth scheme (e.g., for an admin tool managing keys without consuming them).

---

## Optimistic Concurrency

Catches the "two requests modify the same row at the same time, second one silently overwrites the first" failure mode. Each `IConcurrent` entity carries a version token; EF includes it in WHERE clauses on UPDATE; conflicts surface as HTTP 409 instead of silent data loss.

### Mark an entity as concurrent

Two interfaces — pick one per entity:

```csharp
// Cross-provider: needs a RowVersion column (EF migration creates it)
public class Lobby : VEntity<Guid, Guid, Guid>, IConcurrent
{
    public string Name { get; set; } = null!;
    public int MaxMembers { get; set; }
    public byte[] RowVersion { get; set; } = [];   // configured via IsRowVersion automatically
}

// Postgres-native: uses the built-in xmin system column (no migration needed)
public class Lobby : VEntity<Guid, Guid, Guid>, IConcurrentXmin
{
    public string Name { get; set; } = null!;
    public int MaxMembers { get; set; }
    public uint Xmin { get; set; }   // mapped to xmin (xid type), Postgres maintains it
}
```

Both work the same way at the API surface — choice is about whether you want a separate column or use Postgres's built-in.

### What happens on conflict

- `SaveChanges` throws `DbUpdateConcurrencyException`
- `ConcurrencyConflictInterceptor` catches it just before it would propagate
- All registered `IConcurrencyConflictObserver`s are notified
- A `ConflictError` is thrown with metadata `{ kind: "concurrent_update", entityType: "Lobby", entityId: "..." }`
- `VExceptionMiddleware` maps it to HTTP 409 with the standard error envelope

The frontend can branch on `err.error.metadata.kind === "concurrent_update"` to surface "this was changed by someone else, reload?"

### Wire it up

```csharp
services.AddVAppCoreConcurrency(o =>
{
    o.LogConflicts = true;   // logs every conflict at Warning via ILogger
});

services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(connStr);
    options.UseVAppCore<AppDbContext, Guid, Guid>(sp);
    options.AddInterceptors(sp.GetRequiredService<ConcurrencyConflictInterceptor>());
});
```

### Custom observers (metrics, OpenTelemetry, alerts)

Register your own observer in DI before `AddVAppCoreConcurrency()` — it's called alongside the built-in observers:

```csharp
public class PrometheusConflictObserver : IConcurrencyConflictObserver
{
    private readonly Counter _conflictCounter;
    public PrometheusConflictObserver(Counter c) => _conflictCounter = c;

    public void OnConflict(ConcurrencyConflictDetails details)
    {
        _conflictCounter.WithLabels(details.EntityType.Name).Inc();
    }
}

services.AddSingleton<IConcurrencyConflictObserver, PrometheusConflictObserver>();
services.AddVAppCoreConcurrency(o => o.LogConflicts = true);  // logging + metrics both run
```

### Auto-retry helper

For idempotent read-modify-save patterns (counter increments, score updates), retry-on-conflict is exactly the right pattern:

```csharp
await Db.RetryOnConflictAsync(async () =>
{
    var user = await Set.FindAsync(userId);
    user.Score += delta;
    await SaveAsync();
});
```

If the save throws `ConflictError`, the helper clears the change tracker and re-runs the operation (which re-reads fresh data). Defaults to 3 attempts; throws the last `ConflictError` if all fail. Optional `onRetry` callback for logging.

### Force-overwrite helper

For admin overrides or recovery flows where "I know what I'm doing, last write wins":

```csharp
await Db.SaveChangesIgnoreConcurrencyAsync();
```

Re-reads OriginalValues from the DB on conflict so the next save sees no mismatch. Effectively client-wins. Use sparingly — bypassing concurrency control is usually a bug, not a feature.

---

## Rate Limiting

Caps how many requests a client can make per time window — protects auth endpoints from credential-stuffing, public APIs from scraping, expensive endpoints from resource exhaustion. Built on top of .NET's primitives but with VAppCore-shaped conventions: per-user partitioning via `ICurrentUser`, the standard error envelope on rejection, observable hooks, default policy presets, and a Redis backend for multi-instance deployments.

### Wire it up

```csharp
services.AddVAppCoreRateLimiting(o =>
{
    o.LogRejections = true;          // log every 429 at Warning
    o.TierMultipliers["paid"] = 10;   // paid users get 10x the default limits
    o.TierMultipliers["admin"] = double.MaxValue;
    // o.Policies["vauth"] = new RateLimitPolicy("vauth", Capacity: 5, RefillTokensPerSecond: 5.0/60); // default
});

app.UseRouting();
app.UseVRateLimiting();   // must be AFTER UseRouting, BEFORE UseEndpoints/MapControllers
app.MapControllers();
```

### Apply to endpoints

```csharp
[HttpPost, VRateLimit(VAppCoreRateLimitPolicies.Auth)]
public Task<IActionResult> Login(LoginDto dto) { ... }

[HttpPost, VRateLimit(VAppCoreRateLimitPolicies.Mutation, Cost = 5)]
public Task<IActionResult> CreateLobby(CreateLobbyDto dto) { ... }   // 5x weight

[HttpGet, VRateLimit(VAppCoreRateLimitPolicies.Read)]
public Task<IActionResult> ListProducts() { ... }
```

Endpoints without `[VRateLimit]` are not rate-limited.

### Default policies

| Constant | Limit | Intended for |
|---|---|---|
| `VAppCoreRateLimitPolicies.Auth` | 5 / min | login, register, forgot-password, verify-email |
| `VAppCoreRateLimitPolicies.Mutation` | 60 / min | POST/PUT/DELETE on user-owned data |
| `VAppCoreRateLimitPolicies.Read` | 300 / min | GET endpoints |

Override any of them or add new ones via `o.Policies["my-policy"] = new RateLimitPolicy(...)`.

### Per-user partitioning

The default `IRateLimitPartitioner`:
- If the request is authenticated, partitions on `user-{userId}` — limits enforced per user
- Else partitions on `ip-{remoteIp}` — limits enforced per anonymous IP

Override by registering your own `IRateLimitPartitioner` (per-tenant, per-API-key, etc).

### Per-tier multipliers

`TierMultipliers` keyed by role name. Each request, the user's roles are checked; the highest multiplier matched is applied to the policy's capacity AND refill rate. Example: with `["paid"] = 10`, a "paid" user on the `mutation` policy effectively gets 600/min (60 × 10) instead of 60/min. `double.MaxValue` means no limit.

### Rejection response

When the limit is hit:
- HTTP 429
- `Retry-After` header populated with seconds until next refill
- Body is the standard error envelope:
  ```json
  {
    "title": "Rate Limited",
    "titleKey": "server.errors.rateLimited",
    "error": {
      "message": "Rate limit exceeded for policy 'vauth'.",
      "messageKey": "server.errors.rateLimited",
      "metadata": {
        "kind": "rate_limited",
        "policy": "vauth",
        "retryAfterSeconds": 12.4
      }
    }
  }
  ```

Frontend can branch on `err.error.metadata.kind === "rate_limited"`.

### Observers (metrics, alerts)

Same pattern as concurrency observers:

```csharp
public class PrometheusRateLimitObserver : IRateLimitObserver
{
    private readonly Counter _rejectionCounter;
    public PrometheusRateLimitObserver(Counter c) => _rejectionCounter = c;

    public void OnRejected(RateLimitRejection r)
    {
        _rejectionCounter.WithLabels(r.PolicyName, r.RoutePath ?? "").Inc();
    }
}

services.AddSingleton<IRateLimitObserver, PrometheusRateLimitObserver>();
services.AddVAppCoreRateLimiting(o => o.LogRejections = true);  // logging + metrics both run
```

### Programmatic checks

For "should I let the user start this expensive operation" UX flows where you want to gate work BEFORE doing it (and BEFORE consuming a token):

```csharp
public class LobbyController(RateLimitChecker rl, LobbyService lobbies) : ControllerBase
{
    [HttpGet("can-create")]
    public async Task<IActionResult> CanCreateLobby()
    {
        var check = await rl.CheckAsync("mutation", cost: 5);
        return Ok(new { canCreate = check.Permitted, retryAfter = check.RetryAfter });
    }

    [HttpPost, VRateLimit("mutation", Cost = 5)]
    public Task<IActionResult> CreateLobby(...) { ... }   // actual rate limit enforced here
}
```

`CheckAsync` is non-mutating (advisory). `ConsumeAsync` actually decrements — use that when you want to gate work programmatically without an attribute.

### Distributed (Redis) — opt-in

The default `MemoryRateLimitStore` is per-process. With multiple app instances, each holds its own counter — your effective limit becomes `N_instances × per-instance limit`. For real protection at horizontal scale, swap in the Redis store from the `VAppCore.RateLimiting.Redis` sub-package:

```csharp
services.AddVAppCoreRateLimiting(o => { ... });
services.AddVAppCoreRateLimitingRedis("localhost:6379");   // replaces in-memory store
```

The Redis store uses an atomic Lua script for the token-bucket decrement (single round-trip, no lock contention). Buckets auto-expire after enough idle time to fully refill, so unused partitions don't accumulate forever.

### Cost-weighted limits

The `Cost` property on `[VRateLimit]` decrements N tokens per request instead of 1. Useful when one policy's bucket covers multiple endpoint types with different "weights":

```csharp
[VRateLimit("mutation")]                 public Task PostScore(...);    // costs 1
[VRateLimit("mutation", Cost = 5)]       public Task CreateLobby(...);  // costs 5
[VRateLimit("mutation", Cost = 10)]      public Task UploadAvatar(...); // costs 10
```

A user with capacity=60 gets 60 PostScore-equivalents, or 12 CreateLobbies, or 6 avatar uploads — they share the same bucket.

---

## Error Handling

### Custom Errors

All error types extend `BaseError` which extends `Exception`. Each has a fixed status code and structured context.

```csharp
// Available error types:
throw new ValidationError(error);    // 422
throw new NotFoundError(error);      // 404
throw new BadRequestError(error);    // 400
throw new UnauthorizedError(error);  // 401
throw new ForbiddenError(error);     // 403
throw new ConflictError(error);      // 409
throw new BusinessError(error);      // 500
throw new SystemError(error);        // 500
```

ErrorObject structure:

```csharp
throw new NotFoundError(new ErrorObject
{
    Message = "Product not found",              // human-readable
    MessageKey = "products.errors.notFound",    // i18n key
    Metadata = new { ProductId = id }           // arbitrary data
});
```

### VExceptionMiddleware

Registered via `UseVAppCore()`. Catches all exceptions and returns consistent JSON:

**BaseError exceptions** — uses the error's status code and context:

```json
{
  "title": "Not Found Error",
  "titleKey": "server.errors.missingResource",
  "error": {
    "message": "Product not found",
    "messageKey": "products.errors.notFound",
    "metadata": { "productId": "abc-123" }
  }
}
```

**Unhandled exceptions** — returns 500 with system error format:

```json
{
  "title": "System Error",
  "titleKey": "server.errors.system",
  "error": {
    "message": "Object reference not set to an instance of an object.",
    "messageKey": "server.errors.system",
    "metadata": null
  }
}
```

### Validation Errors

ASP.NET model validation errors are automatically converted to the same format. Use data annotations on DTOs:

```csharp
public class CreateProductDto
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; }

    [Range(0.01, double.MaxValue)]
    public decimal Price { get; set; }
}
```

Invalid input returns 422:

```json
{
  "title": "Validation Error",
  "titleKey": "server.errors.validation",
  "error": {
    "message": "One or more validation errors occurred.",
    "messageKey": "server.errors.validation",
    "metadata": {
      "Name": ["The Name field is required."],
      "Price": ["The field Price must be between 0.01 and 1.79769313486232E+308."]
    }
  }
}
```

---

## Response Mapping

### VResponse

Controllers are **required** to wrap responses in `VResponse`. Returning raw entities or unmapped objects is blocked by `VResponseFilter` — this prevents accidental field leaks.

```csharp
// Allowed:
return Ok(VResponse.Map(product, p => new { p.Id, p.Name, p.Price }));
return Ok(VResponse.MapList(products, p => new { p.Id, p.Name }));

// Blocked (throws 500 in development):
return Ok(product);                          // raw entity
return Ok(new { product.Id, product.Name }); // unmapped anonymous object
```

### VResponse.Map

Map a single entity:

```csharp
[HttpGet("{id}")]
public async Task<IActionResult> GetById(Guid id)
{
    var product = await products.GetByIdAsync(id);
    return Ok(VResponse.Map(product, p => new
    {
        p.Id,
        p.Name,
        p.Price,
        p.CreatedAt
    }));
}

[HttpPost]
public async Task<IActionResult> Create(CreateProductDto dto)
{
    var product = await products.Create(dto);
    return CreatedAtAction(nameof(GetById), new { id = product.Id },
        VResponse.Map(product, p => new { p.Id, p.Name, p.Price }));
}
```

### VResponse.MapList

Map a collection:

```csharp
[HttpGet("featured")]
public async Task<IActionResult> GetFeatured()
{
    var items = await products.GetFeatured();
    return Ok(VResponse.MapList(items, p => new { p.Id, p.Name, p.Price }));
}
```

### VPagedResponse (exempt)

`VPagedResponse<T>` from `GetPagedAsync` passes through without wrapping — it's already projected via `VQueryFilter`:

```csharp
[HttpGet]
[UseVQueryParser(typeof(ProductFilter))]
public async Task<IActionResult> GetAll(VQueryParser parser)
{
    // VPagedResponse is allowed — fields controlled by ProductFilter
    return Ok(await products.GetPagedAsync(parser));
}
```

### Reusable Mappings

Define a static mapper to avoid duplication across actions:

```csharp
[ApiController]
[Route("api/products")]
public class ProductController(ProductService products) : ControllerBase
{
    private static readonly Func<Product, object> MapProduct = p => new
    {
        p.Id,
        p.Name,
        p.Price,
        p.CreatedAt
    };

    private static readonly Func<Product, object> MapProductDetail = p => new
    {
        p.Id,
        p.Name,
        p.Price,
        p.CreatedAt,
        p.UpdatedAt,
        Category = new { p.Category.Id, p.Category.Name },
        OrderCount = p.Orders.Count
    };

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var product = await products.GetByIdAsync(id, q => q
            .Include(p => p.Category)
            .Include(p => p.Orders));
        return Ok(VResponse.Map(product, MapProductDetail));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateProductDto dto)
    {
        var product = await products.Create(dto);
        return Ok(VResponse.Map(product, MapProduct));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, UpdateProductDto dto)
    {
        var product = await products.Update(id, dto);
        return Ok(VResponse.Map(product, MapProduct));
    }
}
```

---

## Full Example

A complete setup from entity to endpoint.

### Entity

```csharp
public abstract class AppEntity : VEntity<Guid, Guid, Guid> { }

public class Product : AppEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public Guid CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public List<Order> Orders { get; set; } = [];

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}
```

### DbContext

```csharp
public class AppDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Order> Orders => Set<Order>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
}
```

### VQueryFilter

```csharp
public class ProductFilter : VQueryFilter<Product>
{
    public ProductFilter()
    {
        Field(x => x.Id).Filterable().Sortable().Selectable();
        Field(x => x.Name).Filterable().Sortable().Selectable();
        Field(x => x.Price).Filterable().Sortable().Selectable();
        Field(x => x.CreatedAt).Filterable().Sortable().Selectable();
        Field(x => x.Category.Name).Filterable().Selectable();
        CustomField("orderCount").CountOf("Orders").Selectable().Sortable();
        SetDefaultSort("-createdAt");
        SetDefaultSelect("id", "name", "price", "createdAt");
    }
}
```

### Service

```csharp
public abstract class AppService<T> : VService<T, Guid, Guid, Guid>
    where T : AppEntity { }

public class ProductService : AppService<Product>
{
    public async Task<Product> Create(CreateProductDto dto)
    {
        var exists = await Set.AnyAsync(p => p.Name == dto.Name);
        if (exists)
            throw new ConflictError(new ErrorObject
            {
                Message = "Product already exists",
                MessageKey = "products.errors.duplicate"
            });

        var product = new Product
        {
            Name = dto.Name,
            Price = dto.Price,
            CategoryId = dto.CategoryId
        };
        Set.Add(product);
        await SaveAsync();
        return product;
    }

    public async Task<Product> UpdatePrice(Guid id, decimal price)
    {
        if (price <= 0)
            throw new ValidationError(new ErrorObject
            {
                Message = "Price must be positive",
                MessageKey = "products.errors.invalidPrice"
            });

        var product = await GetByIdAsync(id);
        product.Price = price;
        await SaveAsync();
        return product;
    }
}
```

### DTO

```csharp
public class CreateProductDto
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, double.MaxValue)]
    public decimal Price { get; set; }

    [Required]
    public Guid CategoryId { get; set; }
}
```

### Controller

```csharp
[VAuthorize]
[ApiController]
[Route("api/products")]
public class ProductController(ProductService products) : ControllerBase
{
    private static readonly Func<Product, object> MapProduct = p => new
    {
        p.Id, p.Name, p.Price, p.CreatedAt
    };

    [HttpGet]
    [UseVQueryParser(typeof(ProductFilter))]
    [VAuthorize(Permission = "products.read")]
    public async Task<IActionResult> GetAll(VQueryParser parser)
    {
        return Ok(await products.GetPagedAsync(parser));
    }

    [HttpGet("{id}")]
    [VAuthorize(Permission = "products.read")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var product = await products.GetByIdAsync(id, q => q
            .Include(p => p.Category));
        return Ok(VResponse.Map(product, p => new
        {
            p.Id, p.Name, p.Price, p.CreatedAt, p.UpdatedAt,
            Category = new { p.Category.Id, p.Category.Name }
        }));
    }

    [HttpPost]
    [VAuthorize(Permission = "products.create")]
    public async Task<IActionResult> Create(CreateProductDto dto)
    {
        var product = await products.Create(dto);
        return CreatedAtAction(nameof(GetById), new { id = product.Id },
            VResponse.Map(product, MapProduct));
    }

    [HttpPut("{id}/price")]
    [VAuthorize(Permission = "products.update")]
    public async Task<IActionResult> UpdatePrice(Guid id, [FromBody] decimal price)
    {
        var product = await products.UpdatePrice(id, price);
        return Ok(VResponse.Map(product, MapProduct));
    }

    [HttpDelete("{id}")]
    [VAuthorize(Permission = "products.delete")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await products.DeleteAsync(id);
        return NoContent();
    }
}
```

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default"));
    options.UseVAppCore<AppDbContext, Guid, Guid>(sp);
});

builder.Services.AddControllers();
builder.Services.AddVAppCore<AppDbContext, Guid, Guid>();
builder.Services.AddVServices(typeof(Program).Assembly);

var app = builder.Build();

app.UseVAppCore();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

### Example Requests

```bash
# List with filtering, sorting, cursor pagination, field selection
GET /api/products?filter=price=gt=100;name=like=*Phone*&sort=-price&select=id,name,price,orderCount&limit=10

# Get detail
GET /api/products/abc-123

# Create
POST /api/products
{ "name": "iPhone", "price": 999, "categoryId": "..." }

# Update price
PUT /api/products/abc-123/price
999.99

# Delete (soft)
DELETE /api/products/abc-123
```
