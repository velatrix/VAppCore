# Changelog

## 3.0.0 — 2026-09-26

### Added

- **Scoped authorization.** `[VAuthorize(Scope = "project", Permission = "tasks.view")]` checks the permission in one
  scope — a project, an organization — as the app's new `IScopeAccessResolver` answers for it: a scope the caller may
  not see answers the scope's 404 (the same answer as an id that was never issued), a missing permission 403. Scopes and
  the entities that belong to them are registered with `AddVAuthorization(o => o.AddScope<Guid>(…).AddEntity<Guid>(…))`.
  The scope's id comes from a route value, or through an entity (`ScopeFrom = "taskId"`), in which case a hidden scope
  answers the entity's 404. Every other route value registered as an entity of the scope is bound to it — a foreign id
  answers exactly as a never-issued one — unless the declaration lists it in `Unbound`. Ids are compared in the key
  type's canonical form. The resolver is asked at most once per scope per request.
- `AnyOf` on `[VAuthorize]`: at least one of the permissions (scoped or flat).
- `VAuthorizeHubFilter`: the same declarations on SignalR hubs and hub methods; `ScopeFrom` names a parameter, and a
  refusal is a `HubException` whose message is the refusal's `messageKey`.
- `IVAccess`: the same resolver and memo inside services (`GetAsync`, `RequireAsync`, `RequireAnyAsync`).
- `AddVAuthorization()`: all of the above without `AddVAppCore` (and so without `VResponseFilter`). `AddVAppCore` calls it.
- `VAuthorizationCatalog` (every action and hub method with its declarations) and `app.VerifyVAuthorization()`, which
  refuses to start an app with an undeclared endpoint, an unregistered scope, a `ScopeFrom` that names nothing, an
  unbound route value on a scoped endpoint, or scoped declarations with no resolver.
- `VAuthorizationOptions.Forbidden` replaces the 403 a scoped declaration answers; `ApiKeyAuthenticationType` names the
  API-key identity (default `"ApiKey"`, as before).

### Changed (breaking)

- `VAuthorizeFilter` is an `IAsyncAuthorizationFilter` (it was an `IAsyncActionFilter`), so it runs **before model
  binding**: a caller who is refused gets 401/403/404 where an invalid request used to get its validation error first,
  and a request body — a large upload — is no longer read before the check. Code that called
  `OnActionExecutionAsync` calls `OnAuthorizationAsync`.
- `[AllowAnonymous]` now switches `[VAuthorize]` off on that endpoint, as it does for `[Authorize]`;
  `VerifyVAuthorization` reports an endpoint that declares both.

## 2.2.3 — 2026-09-12

### Fixed

- Page mode now reports `HasMore`. `ApplyWithProjectionAsync` (and therefore `GetPagedAsync` with
  `?page=N`) left the field at its default, so every page claimed to be the last one while `totalPages`
  said otherwise. It is now `page * limit < totalItems`, which is what the unified envelope documents.

## 2.2.2 — 2026-09-12

### Fixed

- Projecting a declared nested field across an **optional** navigation no longer fails the whole page.
  `VQueryParser` emitted the bare access path for nested fields, so a field such as
  `Field(x => x.Order!.CustomerId)` put the NULL a LEFT JOIN produces for a missing relation into a
  non-nullable property. EF Core surfaced that as `InvalidOperationException: Nullable object must have
  a value`, failing every request whose page contained one row without the relation. Nested leaves are
  now wrapped in dynamic LINQ's `np()`, which null-propagates the entire access chain and lifts a value
  type to `Nullable<T>`. Custom navigation fields were already guarded by `iif()` and are unchanged.

  The projected shape is unchanged: the nested object is still emitted, with null members. Strings and
  already-nullable members behave exactly as before, so this is a fix with no contract change.

## 2.2.1 — 2026-05-05

### Added

- `LockedError` (HTTP 423, `titleKey: "server.errors.locked"`) for account-lockout scenarios. Mirrors the shape of the other `BaseError` subclasses; `VExceptionMiddleware` translates it like any other.
