# VAppCore Roadmap

Driven by what Hub (`F:\Projects\IOGames\Hub`) needs to consume VAppCore as the whole game backend.

---

## v1.1.0 — Hub coexistence (blockers)

Goal: VAppCore can be added to Hub without breaking ASP.NET Identity, and the audit/soft-delete/tenant features work alongside `IdentityDbContext`.

### 1. Extract `VDbContext` SaveChanges logic into an `ISaveChangesInterceptor`

**Problem:** `VDbContext` is a base class. Hub already inherits `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>` and C# has no multi-inheritance. Today, Hub cannot use VDbContext at all.

**Fix:** Pull the `SaveChanges`/`SaveChangesAsync` overrides into a new `VAuditInterceptor : ISaveChangesInterceptor`. Any DbContext (including IdentityDbContext) registers it via `optionsBuilder.AddInterceptors(new VAuditInterceptor(...))`, or through a DI helper.

**Scope:**
- Move audit-field logic (CreatedAt, UpdatedAt, CreatedBy, UpdatedBy) out of `VDbContext` into the interceptor
- Move soft-delete logic (`Remove` → `IsDeleted = true` + audit) into the interceptor
- Move tenant assignment for `ITenantScoped<T>` into the interceptor
- Move global query filters (soft-delete, tenant) into a `ModelBuilder` extension: `modelBuilder.ApplyVAppCoreFilters<TUserKey, TTenantKey>()` — Hub calls this from `OnModelCreating`
- Keep `VDbContext` as a convenience class that wires the interceptor + filters automatically (greenfield projects still inherit it for zero ceremony)
- Add a DI helper: `services.AddVAppCoreInterceptors<TUserKey, TTenantKey>()` for projects that can't inherit `VDbContext`

**Acceptance:**
- New unit tests prove the interceptor + filter applied to a vanilla `DbContext` produce identical behavior to `VDbContext`
- Existing `VDbContext` tests still pass (it now just composes the interceptor + filters)
- A new test pairs the interceptor with a fake `IdentityDbContext`-shaped class and proves audit fields populate

### 2. ASP.NET Identity preset for `ICurrentUser`

**Problem:** `ClaimsCurrentUser` defaults to `UserIdClaim = "sub"`, `RoleClaim = ClaimTypes.Role`. Identity uses `ClaimTypes.NameIdentifier` for the user id. Configurable today, but undocumented for the Identity case and easy to get wrong.

**Fix:** Add an opinionated preset: `services.AddVAppCore<TDb>().UseAspNetIdentity()` which sets `UserIdClaim = ClaimTypes.NameIdentifier`, `RoleClaim = ClaimTypes.Role`, `EmailClaim = ClaimTypes.Email`. Document the Identity integration in the README.

**Acceptance:**
- `UseAspNetIdentity()` extension method
- README section "Using with ASP.NET Identity" with a Hub-shaped example
- Test that the preset produces an `ICurrentUser` reading from a Identity-shaped `ClaimsPrincipal`

### 3. Hub migration follow-up (separate work, but tracked here)

Once 1.1.0 ships, Hub does:
- Convert `LocalAuthEndpoints`, `AuthEndpoints`, `MeEndpoint`, `HealthEndpoints` from minimal APIs to MVC controllers
- Add `services.AddControllers()` and `app.MapControllers()` (already present)
- Replace Hub's `LocalAuthErrors` helper with VAppCore's `ConflictError` / `BadRequestError` / etc — error envelope shape changes from `{ type, title, kind, ... }` to `{ title, titleKey, error: { message, messageKey, metadata } }`
- Update frontend `ApiClient.ApiError.kind` getter to read `body.error?.messageKey` instead of `body.kind`
- Update `RegisterForm.vue` and any other component that branches on `err.kind` to use the new key shape
- `ApplicationDbContext` registers `VAuditInterceptor` and calls `ApplyVAppCoreFilters` in `OnModelCreating`
- `ApplicationUser` extends `VEntity<Guid, Guid, Guid>` mixed with `IdentityUser<Guid>` — TBD whether ApplicationUser uses VEntity audit fields or keeps Identity's existing `CreatedAt`/`UpdatedAt`. Probably let Identity own user-table audit and apply VEntity to all *other* entities.

---

## v1.2.0 — Cursor-based pagination

**Problem:** Offset+limit (`page`/`size`) is O(n) on deep pages and unstable when rows insert during paging. Leaderboards and match history need stable cursor paging.

**Scope:**
- New `VQueryParser.ApplyWithCursorAsync<T>(IQueryable<T>, cursorField)` returning `VCursorPagedResponse<T>` with `items`, `nextCursor`, `hasMore`
- Query params: `cursor=<opaque>` and `limit=<n>` (existing `page`/`size` still work)
- Cursor is base64-encoded `{ field: value, id: tiebreakValue }` — opaque to clients
- `VQueryFilter<T>.SetCursor(x => x.CreatedAt)` to declare the default cursor field
- New `VService.GetCursorPagedAsync(parser)` mirroring `GetPagedAsync`

**Acceptance:**
- Tests cover stable pagination under concurrent inserts
- Composable with `filter` and `select`

---

## v1.3.0 — Domain events + transactional outbox

**Problem:** Game state changes need to fan out (push notification on friend invite, Discord webhook on match end, achievement unlock). No mechanism today.

**Scope:**
- `IDomainEvent` marker interface, `VEntity.RaiseEvent(IDomainEvent)` collects events on the entity
- `VAuditInterceptor` (extended) reads collected events on successful SaveChanges, writes them to an `OutboxMessages` table in the same transaction
- `IDomainEventDispatcher` interface + `OutboxProcessor` background service that publishes pending messages and marks them sent
- Pluggable transports: in-memory dispatcher (for handlers in-process), MediatR-style handler discovery
- Failed deliveries → exponential backoff, dead-letter after N tries

**Acceptance:**
- Event raised inside `TransactionAsync` is only persisted if the transaction commits
- Replay: re-running the outbox processor doesn't double-publish (idempotency token)

---

## v1.4.0 — Optimistic concurrency

**Problem:** Multiplayer state edits (lobby settings, friend requests, inventory) lose updates without concurrency tokens.

**Scope:**
- `IConcurrent` interface adding `byte[] RowVersion { get; set; }` (or `uint xmin` for Postgres-native)
- Auto-configured concurrency token in `ModelBuilder` extension
- `VAuditInterceptor` (or a new `ConcurrencyExceptionInterceptor`) catches `DbUpdateConcurrencyException` and rethrows as `ConflictError` with `metadata.kind = "concurrent_update"`

**Acceptance:**
- Concurrent update test produces `ConflictError`
- VService doesn't need any code change to benefit

---

## v1.5.0 — Rate limiting

**Problem:** Auth endpoints (login, register, forgot-password) MUST be rate-limited. Game endpoints (create lobby, send chat) too. .NET 7+ ships `AddRateLimiter` but no convention.

**Scope:**
- `services.AddVRateLimiting()` registers a sensible default policy set: `auth` (5/min/ip), `mutation` (60/min/user), `read` (300/min/user)
- `[VRateLimit("auth")]` MVC filter applies a named policy to a controller/action
- Limit exceeded → `BusinessError` with `metadata.kind = "rate_limited"`, `Retry-After` header

**Acceptance:**
- Auth endpoints use `[VRateLimit("auth")]`
- 6th request in a minute returns 429 with the documented body shape

---

## v1.6.0 — OpenAPI integration

**Problem:** Frontend devs and the eventual game-client team need an OpenAPI spec. `[VAuthorize]` permissions, the RSQL `filter` query param, and the error envelope are invisible to default Swagger output.

**Scope:**
- `services.AddVAppCoreOpenApi()` registers Swashbuckle/Microsoft.OpenApi document filters that:
  - Describe the RSQL `filter`/`sort`/`select`/`page`/`size` params for any endpoint with `[UseVQueryParser<TFilter>]`, including which fields are supported (read from `VQueryFilter<T>`)
  - Decorate operations with `security: [{ vAppAuth: ["permission.name"] }]` from `[VAuthorize]`
  - Add the standard error envelope as a reusable schema referenced by every 4xx/5xx response

**Acceptance:**
- `/swagger/v1/swagger.json` contains the RSQL params for filtered endpoints
- Operation security blocks reflect `[VAuthorize]` attributes

---

## v1.7.0 — Audit log table

**Problem:** `VEntity` only stores last-modified info. Moderation, fraud investigation, and account-deletion compliance need who-changed-what-when history.

**Scope:**
- `IAuditedEntity` opt-in interface (entities the audit log tracks)
- `AuditLog` table: `EntityType`, `EntityId`, `Action` (Add/Modify/Delete), `ChangedAt`, `ChangedBy`, `Changes` (JSONB diff)
- `VAuditInterceptor` (extended) writes audit rows for `IAuditedEntity` saves in the same transaction
- Indexed on `(EntityType, EntityId)` for "show me history for this user" queries

**Acceptance:**
- Modify a tracked entity → one audit row with the field diff
- Delete (soft or hard) → one audit row with action=Delete
- Audit writes don't double-trigger (interceptor is reentrant-safe)

---

## v1.8.0 — Service-to-service / API key auth

**Problem:** When `Core` (the actual game-server library) eventually reports match results back to Hub, you need machine auth — not user cookies.

**Scope:**
- `services.AddVApiKeyAuth()` registers a scheme that reads `X-Api-Key` headers, looks up an `ApiKey` entity (which has a name + hashed secret + permissions), and produces a `ClaimsPrincipal` with `ApiKey` claims
- `[VAuthorize(ApiKey = "game-server")]` requires the API key has the named permission
- Key rotation: keys can be revoked (`IsActive`), expired (`ExpiresAt`)

**Acceptance:**
- Game server posts to `/api/matches` with header → request authorizes
- Revoked key returns 401
- Key list/create/revoke endpoints exist

---

## v2.x backlog (lower priority, separate libs probably)

- Caching helpers (`[CachedFor("5m")]`)
- Mapster integration for DTO mapping
- FluentValidation integration
- Hangfire/Quartz background-job convention
- Blob storage abstraction (Azure / S3 / local)
- Health checks (`AddVHealthChecks`)
- Localization beyond `MessageKey`

These are deferred because:
1. Each is large enough to warrant its own decision
2. Most have good standalone libraries that don't need a VAppCore opinion
3. Adding them dilutes VAppCore's focus (CRUD + filtering + audit + errors)

---

## Versioning policy

- Patch (`1.x.y`) for additive features and bug fixes
- Minor (`1.x.0`) when a new feature lands per this roadmap
- Major (`2.0.0`) reserved for breaking changes to the public API

Each version ships with a CHANGELOG entry, README updates for new features, and tests proving acceptance criteria.
