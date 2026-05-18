# Contributing to OrionAudit

Thanks for your interest! OrionAudit is a small, focused library — clarity and stability matter
more than feature breadth. The bar for changes is high, but the surface area is small, so most
contributions can be reviewed quickly.

## Ground rules

- One change per PR. Refactors, bug fixes, and new features ship separately.
- Tests are not optional. Every behavioural change ships with a test that fails before and passes
  after.
- No new public surface without a release note in `CHANGELOG.md` (under `[Unreleased]`).
- No drive-by reformatting. Style is enforced by `.editorconfig`; respect it but don't reformat
  files unrelated to your change.

## Getting set up

Requirements:

- .NET SDKs **8.0**, **9.0**, **10.0** (multi-targeting; CI builds against all three).
- A SQL provider for integration work — Sqlite ships in-tree via `Microsoft.Data.Sqlite`; no
  external server needed.

Clone, restore, build, test:

```bash
git clone https://github.com/tunahanaliozturk/OrionAudit.git
cd OrionAudit
dotnet restore OrionAudit.sln
dotnet build OrionAudit.sln -c Debug
dotnet test OrionAudit.sln
```

To run the sample console end-to-end:

```bash
dotnet run --project sample/OrionAudit.Sample.Console
```

To run the benchmark (Release-only, sub-second):

```bash
dotnet run --project bench/OrionAudit.Bench -c Release
```

## Layout

```
src/
  OrionAudit/                 # core library (multi-target net8/9/10)
  OrionAudit.AspNetCore/      # HttpContext-based user resolver
  OrionAudit.Testing/         # framework-agnostic test helpers
tests/
  OrionAudit.Tests/           # core unit tests
  OrionAudit.AspNetCore.Tests/
  OrionAudit.Testing.Tests/
  OrionAudit.IntegrationTests/  # Sqlite end-to-end + multi-tenant isolation
sample/
  OrionAudit.Sample.Console/  # runnable demo
bench/
  OrionAudit.Bench/           # BenchmarkDotNet harness
```

## Test framework

Tests use **xUnit v3** with **Microsoft Testing Platform**. Each test project is an executable
(`<OutputType>Exe</OutputType>`) and is invoked directly:

```bash
./tests/OrionAudit.Tests/bin/Debug/net10.0/OrionAudit.Tests.exe
```

`dotnet test` also works (the MTP integration is auto-detected on .NET 10). Tests run in parallel
within a class by default.

## Code style

- Braces on every `if` / `else` (enforced — IDE0011).
- `var` for `new T(...)` and other apparent types; explicit type elsewhere if it aids reading.
- No comments restating what the code does. Comments explain *why* — non-obvious constraints,
  prior incidents, invariants.
- XML doc comments on every public type and member (enforced via `GenerateDocumentationFile` and
  CS1591 in `NoWarn` only for tests).

## Commit / PR conventions

- Conventional commit prefixes: `feat:`, `fix:`, `test:`, `build:`, `docs:`, `ci:`, `chore:`,
  `perf:`. Optional scope, e.g. `feat(capture): ...`.
- PR title mirrors the commit subject; PR description explains *why* and lists notable trade-offs.
- Rebase your branch onto `master` before opening the PR — no merge commits.
- No `Co-Authored-By` trailers unless a real co-author actually authored part of the diff.

## Release process

Releases are cut by maintainers from `master`:

1. Bump `<Version>` in `Directory.Build.props`.
2. Move `[Unreleased]` entries under a dated heading in `CHANGELOG.md`.
3. Tag `vX.Y.Z` and create a GitHub Release.
4. The `release` event in `.github/workflows/ci-cd.yml` publishes the three packages to
   nuget.org and GitHub Packages.

## Reporting bugs / asking questions

Open a GitHub issue with:

- The OrionAudit version, EF Core version, and .NET version.
- A minimal repro (csproj + Program.cs in a single gist is ideal).
- Expected vs. actual behaviour.

Security issues should be reported privately to the package author — see the `Authors` field in
`Directory.Build.props`.
