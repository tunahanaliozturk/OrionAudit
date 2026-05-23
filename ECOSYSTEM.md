# The Orion Family — Ecosystem Plan

> A living vision document for the **Moongazing** .NET package family. Each Orion library is
> standalone — pick what you need, layer the rest in when you do. This file is the strategic
> compass: what's shipped, what's next, what's only an idea, and what shape additions should
> take when they arrive.
>
> **Owner:** Tunahan Ali Ozturk · **NuGet publisher:** `Moongazing` · **Updated:** 2026-05-19

---

## 1. Mission

Build a coherent family of small, sharp, opinionated .NET libraries — each solving one problem
**well enough that you don't have to fork it** — and each adoptable on its own. The "Orion"
name is the marker that you can trust the surface, the docs, and the release discipline. The
`Moongazing.*` namespace + publisher identity is the marker that you can trust who's behind
them.

**Three rules every Orion package follows:**

1. **No transitive grab-bags.** Each package picks the smallest dependency set that does its
   job. If you only want strongly-typed IDs, you do not pay for guard clauses.
2. **Compose, don't merge.** When two Orion packages cross-cut (e.g. Audit + Patch use the
   same diff format), the *contract* is shared via a tiny abstraction package, not by jamming
   them together.
3. **Versioned independently.** A bump in one Orion package never forces a bump in another.
   Cross-package integrations live behind optional `OrionX.Y` adapter packages.

---

## 2. Status snapshot

### Shipped

| Package        | Version | Repo                                                     | Headline                                                                          |
| -------------- | ------- | -------------------------------------------------------- | --------------------------------------------------------------------------------- |
| **OrionGuard** | v6.2    | [tunahanaliozturk/OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) | Fluent guard clauses, validation, security guards, DDD primitives, 9 sub-packages |
| **OrionAudit** | v0.5.0  | [tunahanaliozturk/OrionAudit](https://github.com/tunahanaliozturk/OrionAudit) | EF Core change audit trail (JSON Patch diffs, AOT-clean, opt-in async staging-capture, embedded viewer) |

### Next up

| Package        | Status  | Headline                                                                                                                                    |
| -------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| **OrionKey**   | beta    | Source-generated strongly-typed IDs (Guid, ULID, Snowflake, NanoId) with EF Core / JSON / TypeConverter / IParsable wiring all auto-emitted |

### Planned (already on the family roadmap)

| Package         | Status  | Headline                                                                                              |
| --------------- | ------- | ----------------------------------------------------------------------------------------------------- |
| **OrionPatch**  | planned | Transactional outbox & inbox. JSON Patch diffs match OrionAudit's shape (same format, different sink) |
| **OrionTrace**  | planned | Correlation / causation / tenant / user context propagation across HTTP, MediatR, EF, RabbitMQ, gRPC  |
| **OrionCache**  | planned | Memory + Redis 2-tier cache with entity-change-driven invalidation (Audit-style interceptor)          |
| **OrionFlow**   | planned | Saga orchestration with typed steps, compensations, and OrionTrace-aware correlation                  |

---

## 3. Why each one exists

### OrionGuard *(shipped)*

Guard clauses, validation, security guards (SQL/XSS/path traversal/command injection), DDD
primitives, and a fluent assertions layer for tests. Source-generators, span-based fast paths,
14-language localisation, NativeAOT-ready core. Lives at the input boundary of any service.

**Compose with:** OrionKey (for the existing `[StronglyTypedId]` to graduate out),
OrionTrace (errors carry correlation), OrionAudit (validation failures can be audited).

### OrionAudit *(shipped, v0.2.0)*

EF Core `SaveChangesInterceptor` that captures Insert/Update/Delete as JSON Patch diffs,
supports multi-tenancy, sensitive-field hash/redact, time-travel reconstruction via diff
replay, and v0.2 adds periodic snapshotting + retention sweeps + provider-aware column types.

**Compose with:** OrionKey (type-safe composite keys in v0.4), OrionPatch (audit events can
also publish to an outbox), OrionTrace (auto-captures correlation/tenant/user).

### OrionKey *(next up)*

Source-generated strongly-typed IDs. One attribute, everything else (EF Core ValueConverter,
System.Text.Json converter, `TypeConverter`, `IParsable<T>` / `ISpanParsable<T>`, minimal API
binding) is emitted at compile time. Multiple ID strategies — `Guid`, `long` (Snowflake),
`string` (ULID, NanoId) — selectable via the generic attribute argument.

```csharp
[OrionId<Guid>]                 public readonly partial struct OrderId;
[OrionId<long, Snowflake>]      public readonly partial struct UserId;
[OrionId<string, Ulid>]         public readonly partial struct TenantId;
```

**Why standalone:** Most teams want strongly-typed IDs but don't want a guard library. Today
the feature lives in `OrionGuard.DDD` — graduating it out turns it into a single-purpose
package that's an easy add to any project.

**Compose with:** OrionGuard (the existing `[StronglyTypedId]` in `OrionGuard.DDD` gets
soft-deprecated and re-exports the OrionKey type), OrionAudit (type-safe composite keys),
OrionPatch (typed message keys), OrionFlow (typed saga ids).

### OrionPatch *(planned)*

Transactional outbox and inbox pattern. Writes a domain event next to the EF Core change in
the same transaction, then a hosted dispatcher delivers it to RabbitMQ / Azure Service Bus /
Kafka with at-least-once + idempotency. The event payload uses the **same JSON Patch diff
format as OrionAudit** — so a downstream consumer that already understands an audit row also
understands an outbox event.

**Why it matters:** Most outbox libraries make you marshal your own envelope. OrionPatch pins
the envelope shape to a format you may already produce.

**Compose with:** OrionAudit (publish audit rows as outbox events), OrionTrace (correlation
propagated to consumers), OrionFlow (saga steps emit outbox messages).

### OrionTrace *(planned)*

Cross-cutting context: **correlation id**, **causation id**, **tenant id**, **user id** —
flowed automatically across HTTP middleware, MediatR pipeline, EF Core (via the existing
`AuditScope` pattern), RabbitMQ headers, gRPC metadata. Bridges to OpenTelemetry for the
spans/baggage you already have.

**Why it matters:** Every Orion package needs this same context. Today each one reaches into
`Activity.Current` or `HttpContext.User` independently. OrionTrace centralises the source of
truth so OrionAudit's `IAuditTenantResolver`, OrionPatch's outbox headers, and OrionCache's
tenant-keyed entries all read from one ambient bag.

**Compose with:** everything — this is the cross-cut.

### OrionCache *(planned)*

Two-tier (memory + distributed Redis) typed cache with **automatic invalidation driven by EF
Core entity changes** — the same `SaveChangesInterceptor` pattern OrionAudit uses, but the
side-effect is `cache.Invalidate(typeof(Order), id)` instead of writing an audit row.

**Why it matters:** Cache invalidation is famously hard. OrionCache makes it declarative
(`[CacheInvalidatesOn(typeof(Order))]`) so you don't need to remember to bust the cache by
hand on every mutation path.

**Compose with:** OrionAudit (shared interceptor primitives), OrionKey (typed cache keys),
OrionTrace (tenant-scoped invalidation).

### OrionFlow *(planned)*

Saga orchestration. Typed steps, typed compensations, state persisted via EF Core,
correlation flowing through OrionTrace. Smaller and more opinionated than MassTransit /
NServiceBus — opinionated to the point of being uninteresting for the 5 % of cases that need
a full bus, but invisible-friction for the 95 %.

**Compose with:** OrionPatch (outbox-backed step transitions), OrionTrace (causation chain),
OrionAudit (saga state changes are audited), OrionKey (typed saga ids).

---

## 4. The compose graph

```
                                 ┌──────────────┐
                                 │  OrionTrace  │  (cross-cut)
                                 └─┬──┬──┬──┬───┘
                                   │  │  │  │
       ┌───────────────────────────┘  │  │  └──────────────────────────┐
       │                              │  │                             │
       ▼                              ▼  ▼                             ▼
┌──────────────┐                ┌──────────────┐                ┌──────────────┐
│  OrionGuard  │                │  OrionAudit  │                │  OrionCache  │
│  validation  │                │ change trail │                │ smart layer  │
└──────┬───────┘                └──────┬───────┘                └──────┬───────┘
       │                               │                               │
       │     ┌───────────────┐         │      ┌──────────────┐         │
       └────►│   OrionKey    │◄────────┴─────►│  OrionPatch  │◄────────┘
             │   typed ids   │                │    outbox    │
             └───────┬───────┘                └──────┬───────┘
                     │                               │
                     │       ┌───────────────┐       │
                     └──────►│   OrionFlow   │◄──────┘
                             │     saga      │
                             └───────────────┘
```

Arrows = "depends on" *only* via the adapter packages (`OrionAudit.Patch`,
`OrionCache.Trace`, etc.). The core libraries stay independent.

---

## 5. Suggested ecosystem extensions

Open ideas for siblings beyond the seven already mapped. Each is a candidate — not a
commitment. Listed roughly in order of "would this round out the family in a useful way?"

### Tier 1 — high-leverage, low surface, slot in cleanly

#### `OrionResult` — `Result<T, TError>` + `Error` abstraction

The functional answer to "throw vs return". Tiny: one `Result<T, E>`, one `Error` record,
extension methods for `Bind` / `Map` / `Match` / `Tap`. **Why standalone:** every other Orion
package wants to return failures without throwing (Guard.Try, Patch.TryPublish, Cache.TryGet).
A shared `Result` type means the same error envelope flows through the whole stack.

#### `OrionTime` — `IClock` + business calendars + timezone primitives

`TimeProvider` is the new abstraction in .NET 8+, but it doesn't cover *business* time:
business calendars (skip weekends, holidays), explicit timezone handling, "snap to start of
day in tenant's zone". A few hundred lines of focused code; immediately useful with OrionFlow
(saga timeouts), OrionPatch (delayed dispatch), OrionAudit (`asOf` queries).

#### `OrionMoney` — `Money` value type with currency-safe arithmetic

`decimal Amount + string Currency` is a footgun (you can add USD and EUR with no error).
A typed `Money(decimal, Currency)` with safe arithmetic, formatting, and exchange-rate hooks.
Lives next to OrionKey conceptually — both are "small value-type building blocks". **Why
standalone:** finance teams adopt it without taking anything else.

### Tier 2 — useful but bigger; consider after Tier 1 lands

#### `OrionFeature` — feature flags

Local-first feature flags with optional cloud sync (LaunchDarkly / Unleash / Azure App
Config) behind one interface. Built-in OrionTrace tagging so feature evaluations show up in
spans. **Niche but sticky** — once a team uses one feature-flag library they rarely switch.

#### `OrionRate` — rate limiting primitives

Sliding-window / token-bucket / leaky-bucket implementations with in-memory + Redis backends.
ASP.NET Core has built-in rate limiting now (since .NET 7) — OrionRate is only worth shipping
if it adds something Microsoft.AspNetCore.RateLimiting doesn't: per-tenant quotas via
OrionTrace, audit of rate-limit triggers via OrionAudit. Otherwise skip.

#### `OrionLock` — distributed locks

Redis-backed (Redlock-ish) and PostgreSQL-backed (advisory locks) distributed mutexes with
fencing tokens. Needed once you have OrionFlow + multiple workers. Small, focused.

#### `OrionWebhook` — outbound webhook delivery

Sign, retry, deduplicate, dead-letter. Could be a thin layer on top of OrionPatch (webhooks
are just outbound messages with HTTP transport). **Bundle it instead of forking** if
OrionPatch can natively express "deliver to URL X with signature Y".

### Tier 3 — speculative; ship only if a real consumer asks

#### `OrionExport` / `OrionImport`

CSV / Excel / JSON / PDF export and import with typed schemas + OrionGuard validation. Useful
in B2B SaaS contexts. Big scope (one package per format, easily) — gate on demand.

#### `OrionSearch`

Elasticsearch / Meilisearch / PostgreSQL full-text-search facade. Probably too provider-
specific to abstract cleanly. Likely skip.

#### `OrionGeo` / `OrionPhone` / `OrionAddress`

Domain value types (coordinates, phone numbers, postal addresses) with validation +
formatting. Each is a small, focused package — but the *audience* per package is small. Only
worth it if a single project drives the requirement.

#### `OrionScheduler`

Cron parser + typed job scheduler. The space is crowded (Hangfire, Quartz, NCronJob); adding
another is only worth it if OrionFlow + OrionTrace integration is the differentiator. Defer
until OrionFlow ships.

#### `OrionSecret`

Vault / AWS SSM / Azure KeyVault abstraction with hot reload. Useful but the existing
Microsoft.Extensions.Configuration providers cover most of this. Skip unless an Orion-
specific feature emerges.

### Decline list (don't build these)

- **OrionORM** — EF Core is the line. Don't compete with it.
- **OrionAuth** — IdentityServer / OpenIddict / Microsoft.AspNetCore.Authentication.* cover
  it. Don't fragment further.
- **OrionLog** — Serilog / NLog / Microsoft.Extensions.Logging are mature. Stick with them
  and integrate via OrionTrace.
- **OrionTest** — pick xUnit / NUnit / TUnit. Test helpers should ship *inside* each package's
  own `*.Testing` sub-package (the pattern OrionAudit.Testing established).

---

## 6. Priority recommendation

Order in which to start the next packages, with rationale.

1. **OrionKey v0.1.0** *(MVP scope: Guid only)*
   Smallest unit of work that yields a shippable standalone package. Establishes the
   source-generator project template the rest of the family will reuse. Adopters: any team
   that wants strongly-typed IDs without a guard library.

2. **OrionTrace v0.1.0**
   Unblocks every cross-cutting feature in the planned packages. Without it, each downstream
   package re-invents tenant/user propagation. Ship a minimal core (correlation + tenant
   + user) and grow.

3. **OrionPatch v0.1.0**
   Highest-value runtime package after Trace. Outbox is a well-understood pattern with
   clear scope, and the JSON Patch shape lines up with OrionAudit so the integration story
   is immediate. Bonus: starts the "OrionX.Y adapter package" pattern (`OrionAudit.Patch`
   would emit audit rows as outbox messages).

4. **OrionKey v0.2.0** *(ULID / Snowflake / NanoId strategies)*
   Once the v0.1.0 Guid path is stable, add the alternative strategies. Generator
   infrastructure is already in place.

5. **OrionGuard v6.3 (minor)**
   Soft-deprecate `[StronglyTypedId<T>]` in `OrionGuard.DDD` and document OrionKey as the
   replacement. Backward-compatible; nothing breaks. Concrete removal lands in v7.0.

6. **OrionCache v0.1.0**
   Now that Trace + Audit's interceptor pattern are known good, Cache's invalidation
   interceptor is mostly a port of the audit one with a different side-effect.

7. **OrionAudit v0.3.0 (AOT + source-gen)**
   Defer until after OrionKey lands — OrionKey's generator infrastructure clarifies the
   pattern, and Audit's source-gen reuses the same packaging recipe.

8. **OrionFlow v0.1.0**
   Saga orchestration is the most ambitious of the planned siblings. Saving for last gives
   us OrionPatch, OrionTrace, OrionAudit, and OrionKey to compose, so the saga library can
   stay small.

---

## 7. Conventions (apply to every Orion repo)

### Naming

- **NuGet package id:** `OrionFoo` (no `Moongazing.` prefix — the publisher identity carries
  the brand).
- **Repo name:** `OrionFoo` (singular, no prefix). Owned under `tunahanaliozturk` GitHub
  account.
- **Root CLR namespace:** `Moongazing.OrionFoo` (this is where the brand lives in code).
- **csproj name + folder:** `Moongazing.OrionFoo.csproj` inside `src/Moongazing.OrionFoo/`.
- **Sub-packages:** `OrionFoo.AspNetCore`, `OrionFoo.Testing`, `OrionFoo.MediatR`, etc.
  (drop the `Moongazing.` prefix on the NuGet id, keep it in the namespace).

### Repo skeleton

```
OrionFoo/
├── Directory.Build.props        # Authors=Moongazing, Version, multi-target net8/9/10
├── OrionFoo.sln                 # classic .sln (NOT .slnx — VSCode C# ext doesn't read .slnx yet)
├── .github/workflows/ci-cd.yml  # build-and-test + publish on release event
├── .editorconfig                # CRLF + 4-space indent for C#, 2 for csproj/yml
├── README.md                    # OrionGuard-style: centered logo, badges, comparison table, Quick Start
├── CHANGELOG.md                 # Keep a Changelog format
├── ROADMAP.md                   # version themes + shipped/planned/considered/out-of-scope
├── CONTRIBUTING.md
├── LICENSE.txt                  # MIT
├── ECOSYSTEM.md                 # symlink or copy of this file (cross-repo)
├── docs/
│   ├── logo.png                 # 512×512 PNG, ≤1 MB (NuGet cap)
│   └── superpowers/
│       ├── specs/               # design docs per release
│       └── plans/               # task-by-task implementation plans
├── src/
│   └── Moongazing.OrionFoo/
│       ├── Moongazing.OrionFoo.csproj
│       └── docs/{README.md,logo.png}
├── tests/
│   ├── Moongazing.OrionFoo.Tests/
│   └── Moongazing.OrionFoo.IntegrationTests/
├── sample/
│   └── Moongazing.OrionFoo.Sample.Console/
└── bench/
    └── Moongazing.OrionFoo.Bench/        # BenchmarkDotNet
```

### Multi-targeting

Default to `net8.0;net9.0;net10.0` for libraries; single-target `net10.0` for tests, samples,
benchmarks (otherwise BenchmarkDotNet's auto-generated bootstrap csproj inherits the multi-
target and fails restore — see OrionAudit's `bench/.../Directory.Build.props` for the
workaround).

### Source generators

When a package ships a generator, it lives in the **same NuGet** under `analyzers/dotnet/cs/`,
not as a separate `OrionFoo.Generators` NuGet (split into two packages only if the generator
is independently useful). Generator project targets `netstandard2.0` (Roslyn host
requirement). The repo skeleton extends to:

```
src/
├── Moongazing.OrionFoo/                 # net8/9/10 runtime library
└── Moongazing.OrionFoo.Generators/      # netstandard2.0 analyzer
```

### Release discipline

- SemVer 2.0.0. Public types frozen at v1.0 — breaking changes only on major.
- Tag = `vX.Y.Z`. GitHub Release notes pulled from `CHANGELOG.md` (the `awk` recipe in each
  ci-cd.yml).
- One Conventional Commit per logical change (`feat:` / `fix:` / `perf:` / `refactor:` /
  `docs:` / `test:` / `build:` / `ci:` / `chore:`). Optional scope, e.g. `feat(capture):`.
- No `Co-Authored-By` trailers unless a real co-author actually authored.

### CI/CD template

Three jobs:

1. **build-and-test** — restores, builds Release across all SDKs, runs `dotnet test`.
2. **aot-publish-check** *(optional; ship once a package targets AOT)* — `dotnet publish` of
   the sample with `PublishAot=true`; fails on `IL2*` / `IL3*` / `AOT0*` warnings.
3. **publish** *(triggers on `release` event)* — packs and pushes to nuget.org +
   GitHub Packages. Requires `NUGET` repo secret.

### Documentation

Each package's README mirrors OrionGuard's structure:

1. Centered logo + badges (NuGet version, downloads, license, target frameworks)
2. One-line hero callout ("v0.X.0 is here!")
3. **Why FooPackage?** comparison table vs. existing alternatives
4. Quick Start in 30-60 seconds
5. Ecosystem packages table
6. Per-feature sections with code snippets
7. Performance section with BenchmarkDotNet numbers
8. Roadmap pointer + contributing pointer + license

---

## 8. Open strategic questions

These don't need answers today; revisit during package planning.

- **Single mega-monorepo vs. one-repo-per-package?** Current plan is one-repo-per-package
  (consistent with `tunahanaliozturk/OrionGuard` + `tunahanaliozturk/OrionAudit`). The cost
  is duplicating the build skeleton; the benefit is independent versioning + smaller blast
  radius per PR.

- **Adapter packages (`OrionAudit.Patch`, `OrionCache.Trace`, etc.) — where do they live?**
  Option A: in the *outgoing* package's repo (`OrionAudit` ships `OrionAudit.Patch`). Option
  B: in the *incoming* package's repo (`OrionPatch` ships `OrionAudit.Patch`). Option C: a
  shared `OrionFamily.Adapters` repo. Lean toward A — keeps the cognitive load with the
  team most likely to break the integration.

- **License consistency.** All MIT today. Worth keeping uniform so a consumer can pick any
  Orion package without legal review.

- **Localisation.** OrionGuard supports 14 languages. None of the other packages currently
  need this — but the *infra* (resource loader, language packs) might be worth extracting
  into a tiny `OrionLocalize` package if more than one sibling needs it.

- **Telemetry conventions.** OrionAudit uses `OrionAudit` as ActivitySource + Meter name;
  every future Orion package should follow the same `Orion{Foo}` convention so OTel
  collectors can subscribe to all of them with one pattern.

---

## 9. How to evolve this document

This file is meant to be edited across sessions, not frozen. When something here changes:

- A package ships → move it from *Planned* → *Shipped*, update version, link the release.
- A new sibling becomes a real plan → move it from *Suggested extensions* → *Planned*, add
  it to the compose graph.
- A sibling gets cut → move it to the *Decline list* with a one-line "why we said no".
- Conventions evolve → patch §7. Treat this as the source of truth that downstream repos
  follow.

If a session starts asking "should I build X?", the answer is in §5 or §8 here, not in any
single repo's ROADMAP.

---

*End of document. The next move is yours — pick an entry from §6's priority list and open
its repo.*
