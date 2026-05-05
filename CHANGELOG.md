# Changelog

## 2.2.1 — 2026-05-05

### Added

- `LockedError` (HTTP 423, `titleKey: "server.errors.locked"`) for account-lockout scenarios. Mirrors the shape of the other `BaseError` subclasses; `VExceptionMiddleware` translates it like any other.
