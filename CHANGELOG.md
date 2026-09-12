# Changelog

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
