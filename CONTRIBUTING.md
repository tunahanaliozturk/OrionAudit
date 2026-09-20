# Contributing to OrionAudit

Thanks for taking the time to look at this. OrionAudit captures EF Core entity changes as RFC 6902 JSON Patch documents in an audit log table. The project is small and the bar for contributions is "does it make the package clearer, faster, or safer without expanding the public surface needlessly."

## Before you open a PR

For anything beyond a typo, a docs tweak, or a one-line fix, please open an issue first. Five minutes of alignment up front saves an afternoon of rework later. State:

- The use case you are trying to solve
- What you tried that did not work
- Whether you want to send the patch yourself or are flagging the gap

For typos, docs polish, comment fixes, single-line changes, please skip the issue and send a PR directly. Title it `docs: ...` or `chore: ...` so it is obvious from the queue.

## Local development

```bash
git clone https://github.com/tunahanaliozturk/OrionAudit
cd OrionAudit
dotnet restore
dotnet build -c Release
dotnet test
```

.NET 8 SDK is required. Multi-target builds may need 9.0 / 10.0 SDKs installed; the multi-target dimension is intentional and not optional.

Branch from `master`. Name the branch after intent: `feat/...`, `fix/...`, `docs/...`, `refactor/...`, `chore/...`, `test/...`.

## Pull request shape

- One conceptual change per PR. Refactors and behaviour changes go in separate PRs even if the diff feels small.
- Conventional Commits style commit subject (`feat:`, `fix:`, `docs:`, etc.).
- New behaviour comes with tests. Bug fixes come with a failing-before, passing-after test.
- Public API additions need XML doc comments and a `PublicAPI.Unshipped.txt` entry (see [Public API](#public-api)). Breaking changes need a CHANGELOG entry.
- No `Co-Authored-By` trailers. The author of the PR is the author of the work.

## Coding style

- The repo enforces analyzer warnings as errors and `AllEnabledByDefault` analysis mode. Treat warnings as bugs.
- Match the surrounding code style. The repo does not have a separate STYLE.md; if the existing code does X, do X.
- Names are spelled out. No `mgr`, `svc`, `ctx`. The exceptions are well-known abbreviations (`Id`, `Db`, `Url`, `Json`).
- Comments explain why, not what. The code already says what.

## Public API

OrionAudit 1.0.0 froze the public surface: every breaking change from here requires a major
version. `Microsoft.CodeAnalysis.PublicApiAnalyzers` enforces that. Each of the five packable
projects carries two files next to its `.csproj`:

- `PublicAPI.Shipped.txt` — the surface as the last release shipped it. **Do not hand-edit this
  file.** It changes only at a release cut.
- `PublicAPI.Unshipped.txt` — public API added since that release. Starts at `#nullable enable`.

Because warnings are errors, the build tells you when the files are wrong:

- **RS0016** — you made something public that is in neither file. Add it to `PublicAPI.Unshipped.txt`.
- **RS0017** — a file names something the code no longer has. You removed or changed public API,
  which is a breaking change; if that is deliberate, it belongs in a major-version PR.

Let the analyzer write the entries rather than typing them — the format and sort order are exact:

```bash
dotnet format analyzers src/Moongazing.OrionAudit/Moongazing.OrionAudit.csproj --diagnostics RS0016
```

At a release cut, the `Unshipped` entries move into `Shipped` and `Unshipped` goes back to a lone
`#nullable enable`.

## Tests

- xUnit + FluentAssertions.
- Test names are sentences with underscores: `Account_withdraw_throws_when_insufficient_funds`.
- Integration tests that need infrastructure go in a separate test project, gated by Testcontainers or `Skip` attributes when the infrastructure is unavailable.
- Coverage is a side effect of writing tests for behaviour, not a target in itself.

## Reporting bugs

Open an issue with:

- A minimal reproduction (one file, one method, ideally less than 50 lines)
- The actual behaviour vs the expected behaviour
- The runtime (`dotnet --info` output) and the package version

If the bug has security implications, please email the maintainer privately before opening a public issue.

## Security

Do not file public issues for vulnerabilities. Contact the maintainer directly. See [SECURITY.md](SECURITY.md) if present, otherwise email the address listed in the package NuGet metadata.

## Conduct

Be kind. We follow the [Code of Conduct](CODE_OF_CONDUCT.md). Disagreement is fine; rudeness is not.

## License

By submitting a pull request, you agree your contribution is licensed under the repo's [MIT License](LICENSE).
