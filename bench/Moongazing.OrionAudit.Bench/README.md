# OrionAudit.Bench

[BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks for OrionAudit's hot paths.

## Run

Release mode only (BenchmarkDotNet refuses Debug):

```bash
dotnet run -c Release --project bench/OrionAudit.Bench --
```

Pass `--filter "*"` to run all, or e.g. `--filter "*DiffEngine*"` to run a subset.

## What's measured

| Benchmark             | Hot path                                                           |
| --------------------- | ------------------------------------------------------------------ |
| `SnapshotBuilderBench`| Per-entity JSON snapshot build, with and without Hash/Redact rules |
| `DiffEngineBench`     | JSON Patch Compute and Apply across 4 / 16 / 64 properties         |
| `InterceptorBench`    | EF SaveChanges with vs. without OrionAudit, batch sizes 1/10/100   |
| `ReconstructorBench`  | `ReconstructAsync` over 10 / 100 / 1000 audit rows                 |

## Reading the output

- **Mean** — average time per operation (use the unit BenchmarkDotNet picks).
- **Allocated** — bytes allocated per op; the most useful column for hot-path tuning.
- **Ratio** — relative to the `[Baseline]` benchmark in the same class.

The `InterceptorBench` baseline is "no audit"; the audit row shows the per-SaveChanges overhead
OrionAudit adds. Treat the absolute numbers as Sqlite-bound rather than representative of a real
SQL Server / Postgres deployment — the *ratio* is the portable signal.
