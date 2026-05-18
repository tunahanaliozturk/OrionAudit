# Changelog

All notable changes to OrionAudit will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-19

Initial public release of OrionAudit.

### Packages

- `OrionAudit` — core library
- `OrionAudit.AspNetCore` — ASP.NET Core integration
- `OrionAudit.Testing` — framework-agnostic test helpers

### Added

- `AuditSaveChangesInterceptor` — EF Core interceptor that captures Insert / Update / Delete
  operations against audited entities and writes `AuditLog` rows in the same transaction.
- JSON Patch (RFC 6902) diff engine via `JsonPatch.Net` for compact, replayable change records.
- Sensitive-field handling via `[NotAuditable]`, `[HashedAudit]`, and `[RedactedAudit]` attributes,
  plus equivalent fluent overrides (`b.Exclude(...)`, `b.Hash(...)`, `b.Redact(...)`).
- Fluent configuration surface (`AuditConfigurationBuilder`, frozen `AuditConfiguration` runtime
  view) with attribute-discovered defaults and explicit overrides.
- Assembly scanning via `AuditableTypeDiscovery` and `OrionAuditOptions.ScanAssembly(...)`.
- Pluggable user / tenant attribution: `IAuditUserResolver`, `IAuditTenantResolver`, `AuditUser`
  record.
- Read API: `DbContext.AuditFor<T>()` and `DbContext.AuditLog()` extensions with automatic
  tenant filtering (bypassable with `crossTenant: true`).
- `IAuditReconstructor` with `ReconstructAsync` and `ReconstructManyAsync` for time-travel
  state reconstruction by diff replay.
- DI surface: `AddOrionAudit<TContext>`, `UseOrionAudit`, `ApplyOrionAuditConfigurations`.
- OpenTelemetry instrumentation via `OrionAuditTelemetry.ActivitySource` and `Meter` with capture
  / reconstruct activities and counters/histograms (`orionaudit.entries.written`,
  `orionaudit.entries.failed`, `orionaudit.capture.duration`, `orionaudit.reconstruct.duration`).
- ASP.NET Core integration: `HttpContextAuditUserResolver`, `AddOrionAuditAspNetCore()`.
- Testing helpers: `AuditCapture` snapshot + `AuditAssertions` fluent surface
  (`HaveLogged<T>`, `NotHaveLogged<T>`, `HaveLoggedExactly(n).Of<T>()`), plus `InMemoryAuditUserResolver`
  and `InMemoryAuditTenantResolver` test doubles.

### Limitations (v0.1.0)

- Composite primary keys throw `OrionAuditConfigurationException` at runtime — single-column PKs only.
- Snapshot column populated only on Delete; in-place reconstruction at any timestamp uses diff
  replay from Insert forward.
