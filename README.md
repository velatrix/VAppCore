# VAppCore

Enterprise .NET 8 library for building web APIs. Provides base entities with audit fields, authentication/authorization, RSQL query parsing with field-level control, service base class, structured error handling, and response mapping enforcement.

## Table of Contents

- [Quick Start](#quick-start)
- [Entity Foundation](#entity-foundation)
- [VDbContext](#vdbcontext)
- [Authentication & Authorization](#authentication--authorization)
- [VService](#vservice)
- [Query Parsing (RSQL)](#query-parsing-rsql)
- [VQueryFilter](#vqueryfilter)
- [Error Handling](#error-handling)
- [Response Mapping](#response-mapping)
- [Full Example](#full-example)

---

## Quick Start

### Install

From local feed:

```bash
dotnet add package VAppCore
```

Make sure your `nuget.config` includes the local source:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="local" value="F:\Packages\C#" />
  </packageSources>
</configuration>
```

### Minimal Setup

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddControllers();

builder.Services.AddVAppCore<AppDbContext>(options =>
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

    // Optional — add this property and VDbContext sets it automatically:
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

Opt-in interface for multi-tenancy. VDbContext automatically:
- Sets `TenantId` from the current user on new entities
- Applies a global query filter — queries only return data for the current tenant

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

## VDbContext

### Setup

Inherit from `VDbContext<TKey, TUserKey, TTenantKey>`:

```csharp
public class AppDbContext : VDbContext<Guid, Guid, Guid>
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();

    public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider sp)
        : base(options, sp) { }
}
```

### What it does automatically on SaveChanges

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
    bool IsInRole(string role);
    bool HasPermission(string permission);
}
```

### ClaimsCurrentUser (Default)

The library provides `ClaimsCurrentUser` — reads from `ClaimsPrincipal`, works with any auth mechanism (JWT, Cookie, OpenID Connect, ASP.NET Identity).

Configured via `AddVAppCore`:

```csharp
builder.Services.AddVAppCore<AppDbContext>(options =>
{
    options.UserIdClaim = "sub";           // default
    options.TenantIdClaim = "tenant_id";   // default
    options.RoleClaim = ClaimTypes.Role;    // default
    options.PermissionClaim = "permission"; // default
    options.EmailClaim = "email";           // default
});
```

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

VQueryParser reads `filter`, `sort`, `select`, `page`, and `size` from query parameters and applies them to an `IQueryable`.

### Query Parameters

| Parameter | Example | Description |
|-----------|---------|-------------|
| `filter` | `name==John;age=gt=25` | RSQL filter expression |
| `sort` | `-createdAt,+name` | `+` ascending, `-` descending |
| `select` | `id,name,email` | Fields to include in response |
| `page` | `2` | Page number (1-based, default: 1) |
| `size` | `20` | Page size (default: 20, max: 100) |

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
// Filter + sort + pagination (no field selection):
IQueryable<T> result = parser.Apply(queryable);

// Filter + sort + pagination + total count:
var (items, totalCount) = await parser.ApplyWithCountAsync<T>(queryable);

// Filter + sort + pagination + field projection (uses VQueryFilter selectable fields):
VPagedResponse<object> result = await parser.ApplyWithProjectionAsync<T>(queryable);

// Individual operations:
queryable = parser.ApplyFilter(queryable);
queryable = parser.ApplySort(queryable);
queryable = parser.ApplyPagination(queryable);
```

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

# Pagination
GET /products?page=2&size=25

# Everything combined
GET /products?filter=price=gt=100;status==Active&sort=-createdAt&select=id,name,price&page=1&size=10
```

Response format for `ApplyWithProjectionAsync`:

```json
{
  "items": [
    { "id": "...", "name": "iPhone", "price": 999 },
    { "id": "...", "name": "MacBook", "price": 1999 }
  ],
  "page": 1,
  "size": 10,
  "totalItems": 42,
  "totalPages": 5
}
```

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
public class AppDbContext : VDbContext<Guid, Guid, Guid>
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Order> Orders => Set<Order>();

    public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider sp)
        : base(options, sp) { }
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

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddControllers();
builder.Services.AddVAppCore<AppDbContext>();
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
# List with filtering, sorting, pagination, field selection
GET /api/products?filter=price=gt=100;name=like=*Phone*&sort=-price&select=id,name,price,orderCount&page=1&size=10

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
