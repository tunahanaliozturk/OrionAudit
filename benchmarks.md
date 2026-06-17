# OrionAudit Benchmarks

A BenchmarkDotNet suite that measures OrionAudit's pure, in-memory hot paths: snapshot
building, RFC 6902 diff compute/apply, the replay fold at the heart of time-travel
reconstruction, and the read-side view projection.

The suite deliberately avoids any benchmark that needs a database, MySql provider, or an
ASP.NET host. Those paths are dominated by IO and a microbenchmark of them measures the
storage engine, not OrionAudit. Everything here runs against in-memory `JsonObject` /
`AuditLog` state so the numbers reflect OrionAudit's own CPU and allocation cost.

No reference numbers are published here on purpose. Results are hardware-, runtime-, and
JIT-dependent. Run the suite on your own machine (see [Running](#running)) and read the
BenchmarkDotNet summary table it prints.

## Methodology

- Each benchmark class is decorated with `[MemoryDiagnoser]`, so every result row reports
  allocated bytes and GC counts alongside timing.
- Each class runs on two runtimes via `[SimpleJob(RuntimeMoniker.Net80)]` and
  `[SimpleJob(RuntimeMoniker.Net90)]`, so .NET 8 and .NET 9 appear side by side in the
  summary table.
- Inputs are built once in `[GlobalSetup]`; the measured `[Benchmark]` methods do only the
  work under test.
- No shared state between scenarios; no database, no DI container, no EF Core in the
  measured path.

## Scenarios

### `SnapshotBuilderBenchmarks`

Measures `SnapshotBuilder.Build`, the per-entity snapshot hot path invoked once for every
audited entity on every `SaveChanges`. Compares an attributes-only capture (`baseline`)
against a configuration that hashes one field and redacts another, so the delta isolates
the SHA-256 hash path. Pure: a property-value dictionary in, a `JsonObject` out.

### `DiffEngineBenchmarks`

Measures the RFC 6902 diff engine, parameterized by property count (4, 16, 64).
`Compute` is the write-side path (one diff per audited entity per `SaveChanges`); `Apply`
is the read-side path (one diff applied per audit row during reconstruction). Both are pure
`JsonObject` transforms.

### `ReplayBenchmarks`

Measures the pure replay loop at the heart of time-travel reconstruction: folding a chain
of N pre-computed RFC 6902 patches into a starting snapshot via repeated `DiffEngine.Apply`.
This is the same fold the database-backed reconstructor performs once it has loaded the
audit rows, with the storage IO removed. Parameterized by event count (10, 100, 1000) to
show how replay scales with history depth.

### `AuditViewRenderBenchmarks`

Measures the read-side projection that turns persisted `AuditLog` rows into human-readable
`AuditEntryView`s. `RenderOne` parses one row's diff into field-level changes;
`RenderMany` orders and projects a page of rows. Parameterized by row count (1, 50, 500).
Pure: depends only on `System.Text.Json`.

## Running

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionAudit.Benchmarks
```

`Program.cs` uses `BenchmarkSwitcher.FromAssembly(...)`, so with no arguments it prompts for
which class(es) to run. To run everything non-interactively:

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionAudit.Benchmarks -- --filter '*'
```

To run a single class, pass its name to the filter, for example:

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionAudit.Benchmarks -- --filter '*DiffEngineBenchmarks*'
```

Results, including machine-readable exports, are written to `BenchmarkDotNet.Artifacts/results/`.

## Reading the results

- Compare the `Mean` column within a class, not across machines.
- Watch the `Allocated` column: OrionAudit's design goal on these paths is low, predictable
  allocation. The snapshot primitive fast path and the reflection-free diff engine are the
  reason the numbers stay flat as inputs grow.
- For `ReplayBenchmarks`, expect cost to grow with event count: reconstruction is O(N) in
  audit-row count when no snapshot cursor is present. Periodic snapshotting (shipped in the
  core library) turns this into O(K) where K is the number of updates since the last
  snapshot; that win is exercised by the database-backed paths, not by this pure suite.
