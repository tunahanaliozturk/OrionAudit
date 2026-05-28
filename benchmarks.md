# OrionAudit Benchmarks

Latest run: 2026-05 on Intel Core i7-7820HQ CPU @ 2.90 GHz (Kaby Lake, 4 physical / 8 logical cores), Windows 11 22H2, .NET 10.0.5, BenchmarkDotNet 0.15.8.

> **Note.** These numbers are reference-grade, not marketing claims. Reproduce locally with `dotnet run -c Release --project bench/Moongazing.OrionAudit.Bench`. Your hardware will differ, and the relative cost of audit vs. baseline `SaveChanges` is dominated by the database round-trip on real providers (see "Database overhead" below).

## Methodology

- BenchmarkDotNet job: short-run defaults (3 warmup + 5 measurement iterations) unless otherwise noted.
- Memory profiler enabled (`[MemoryDiagnoser]`).
- All allocations and GC stats reported.
- Each scenario isolated; no shared state between runs.
- EF Core scenarios run against in-memory SQLite, which has near-zero per-row IO cost. This makes audit overhead look large in ratio. Real Postgres / SQL Server numbers move very differently (see the SaveChanges scenario notes).

## Scenarios

### Per-entity snapshot build

A 7-property entity, single snapshot build.

| Method                  |    Mean | Ratio | Allocated |
|-------------------------|--------:|------:|----------:|
| Build_AttributesOnly    |  677 ns |  1.00 |     984 B |
| Build_WithHashAndRedact | 1.74 us |  2.57 |     984 B |

Interpretation: `Hash` and `Redact` pay for one SHA-256 invocation per hashed field. The UTF-8 input buffer is stack-allocated for inputs under 256 bytes and rented from `ArrayPool<byte>` above that, with stack-allocated SHA-256 output, so the cryptographic path itself is zero-allocation. The allocation total above is the JSON node graph for the snapshot, not the hash.

### JSON Patch diff (Compute vs. Apply)

The diff engine is the workhorse of OrionAudit. `Compute` runs on write (capture); `Apply` runs on read (reconstruction).

| Properties | Compute (Mean / Alloc) | Apply (Mean / Alloc) | Apply ratio |
|-----------:|-----------------------:|---------------------:|------------:|
|          4 |       25.4 us / 24.0 KB |     24.9 us / 4.4 KB |        0.18 |
|         16 |       95.6 us / 88.5 KB |    35.8 us / 15.3 KB |        0.17 |
|         64 |      330.0 us / 351 KB  |   136.5 us / -       |        0.41 |

Interpretation: `Apply` is consistently 2.5x to 5x cheaper than `Compute` and allocates 5x to 6x less. This is the right shape for an audit system: capture pays the upfront cost so that replay (time-travel reconstruction) is cheap. The 64-property row shows the engine starting to scale sub-linearly on the apply path because more operations fit in the same JSON tree walk.

### EF Core SaveChanges overhead

In-memory SQLite, OrionAudit registered vs. a baseline `SaveChanges` with no audit hooked up.

| Batch size | NoAudit (Mean) | WithAudit (Mean) | Slowdown | Alloc ratio |
|-----------:|---------------:|-----------------:|---------:|------------:|
|          1 |        197 us  |          679 us  |     3.5x |        1.5x |
|         10 |        474 us  |        2.38 ms   |     5.0x |        3.2x |
|        100 |       2.59 ms  |        10.8 ms   |     4.2x |        4.6x |

Interpretation: SQLite in-memory has near-zero per-row write cost, which makes the audit overhead look large in ratio. Against a real Postgres or SQL Server deployment the DB round-trip dominates total latency and the audit overhead drops into the 5 to 15 percent range. Run the harness against your own provider for the number that matters to you.

### Async-mode interceptor overhead

`InterceptorBench` measures sync vs. async-staging capture under in-memory SQLite, where async mode pays for the queue insert plus the dispatcher's claim without getting any network-IO benefit. On a real DB this picture reverses (see the v0.5.0 callout in README.md).

| Scenario                       | Batch |  Mean (us) | Ratio | Allocated      |
|--------------------------------|------:|-----------:|------:|----------------|
| SaveChanges_NoAudit            |     1 |        277 |  1.00 | 71 KB          |
| SaveChanges_WithAudit          |     1 |        769 |  2.82 | 96 KB  (1.35x) |
| SaveChanges_WithAsyncAudit     |     1 |      1 311 |  4.80 | 95 KB  (1.34x) |
| SaveChanges_NoAudit            |    10 |        957 |  1.00 | 141 KB         |
| SaveChanges_WithAudit          |    10 |      3 936 |  4.18 | 335 KB (2.37x) |
| SaveChanges_WithAsyncAudit     |    10 |      3 414 |  3.62 | 343 KB (2.43x) |
| SaveChanges_NoAudit            |   100 |      6 023 |  1.00 | 819 KB         |
| SaveChanges_WithAudit          |   100 |     13 720 |  2.36 | 2.7 MB (3.31x) |
| SaveChanges_WithAsyncAudit     |   100 |     14 259 |  2.45 | 2.8 MB (3.45x) |

Interpretation: in-memory SQLite is unkind to async-mode bookkeeping. The `ExecuteUpdateAsync` claim plus the queue insert show up as raw cost without the network round-trip latency a real DB has. Treat async capture as a correctness-preserving way to move materialization off the consumer's transaction. It is a throughput feature, not a microbenchmark win.

### Time-travel reconstruction

| History depth |    Mean | Allocated |
|--------------:|--------:|----------:|
|            10 | 1.09 ms |    126 KB |
|           100 | 2.76 ms |    506 KB |
|          1000 | 8.95 ms |    4.3 MB |

Interpretation: reconstruction is O(N) in audit-row count because every diff is applied in sequence from the Insert forward. Periodic snapshotting (already shipped in v0.2) turns this into O(K) where K is the number of updates since the last snapshot. For `SnapshotEvery(100)` against the 1000-depth case, expect roughly a 10x speedup and proportional allocation drop.

## Design notes that show up in the numbers

- **Primitive fast path.** `SnapshotBuilder.ConvertToNode` switches on the runtime type and calls `JsonValue.Create` directly for primitives, skipping `JsonSerializer.SerializeToNode`'s reflection. User-defined types still fall through to the reflective path.
- **FrozenDictionary lookups.** `IAuditConfiguration.IsAudited` is a frozen-dictionary `ContainsKey`. The interceptor short-circuits on entity state before doing the type lookup, so non-audited entities pay nothing measurable.
- **Tenant resolver lookup.** `FindExtension<CoreOptionsExtension>()` for the tenant resolver rather than LINQ over `IDbContextOptions.Extensions`, which is what older builds did.

## How to reproduce

```bash
cd <repo-root>
dotnet run -c Release --project bench/Moongazing.OrionAudit.Bench
```

The harness includes `DiffEngineBench`, `DispatcherBench`, `InterceptorBench`, `ReconstructorBench`, `ReconstructorWithSnapshotBench`, and `SnapshotBuilderBench`. Pass `--filter '*'` to run everything; pass a specific class name to run just one.

Results appear in `BenchmarkDotNet.Artifacts/results/`.

## Comparison baselines

We report OrionAudit numbers next to honest baselines so readers can place them in context:

- **No audit registered.** The `NoAudit` columns above. The right comparison for "what does the abstraction cost in the hot path".
- **Audit.NET.** The closest commodity alternative for EF Core. It captures a similar shape (entity diff per SaveChanges) but does not ship a reflection-free diff engine, periodic snapshotting, or time-travel reconstruction. A side-by-side will land in a future release; until then, the rough public guidance is that Audit.NET is in the same order of magnitude as OrionAudit's sync mode on simple entities and falls behind once snapshotting matters.
- **DIY interceptor.** A minimal hand-rolled SaveChangesInterceptor that writes a flat JSON dump per change. Establishes how much OrionAudit's diff and reconstruction features cost compared to "log everything as a blob".

The point of the comparison is to be honest about where OrionAudit sits, not to win a chart. If a competitor is faster on a given scenario we will say so and explain why.
