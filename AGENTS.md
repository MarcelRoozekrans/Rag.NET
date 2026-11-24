# AGENTS.md: Rag.NET

Rag.NET is a modular RAG pipeline library for .NET, built on Microsoft.Extensions.AI: ingestion,
chunking, vector stores, retrieval and chat. It ships 74 NuGet packages from `src/`; applications
install the two or three they need. `docs/` is also the Docusaurus site, published to GitHub Pages.

## Product-line standards

This repository is part of a .NET product line. Read the [product-line standards](https://github.com/MarcelRoozekrans/product-line/blob/main/standards.md) before changing anything: they hold the rules for every product, including how to build (a failing test first, the cause before a fix, build and test output before calling work done) and what may appear in commits, pull requests and issues. Build sessions do not use superpowers. This file adds what is specific to this repository.

## Layout

- Only `src/` ships. `src/Rag.NET.Benchmarks.Quality` is the one `src/` project with `IsPackable`
  false. `tests/`, `benchmarks/` and `samples/` never pack, and a test guards it.
- Every packable project needs its own `README.md` beside its `.csproj`, a `<Description>` and a
  `<VerifiedBy>`. A missing README fails pack with NU5039 on purpose; the root README is not a
  fallback. Each package README opens with the shared badge header, which a test enforces.
- Dependency versions live only in `Directory.Packages.props`, pinned, never floating.
- `docs/reference/features.md` is the feature ledger. A section marked Done may name only packages
  that exist under `src/`. A new page under `docs/` must be listed in `sidebars.ts` and in the
  Pages table of `docs/index.md`.
- The connector tests replay hand-written WireMock cassettes under `Cassettes/`. A cassette must hold
  no credential. If a real recording shows a connector is broken, fix the connector, not the cassette.

## Build and test

Prerequisites: the .NET 11 SDK, which builds both targets, and the .NET 10 and .NET 11 runtimes.
Libraries and most test projects target `net10.0;net11.0`, so tests run once per runtime. Exceptions:
`Rag.NET.Benchmarks` targets `net10.0` only. The samples, `Rag.NET.Benchmarks.Quality.GraphExtractions`,
`Rag.NET.Benchmarks.Quality.Hypotheticals` and the tests `Rag.NET.Benchmarks.Quality.Tests` and
`Rag.NET.Benchmarks.Quality.IntegrationTests` target `net11.0` only. The only `global.json` selects
Microsoft.Testing.Platform as the `dotnet test` runner and pins no SDK. `LangVersion` is `preview`
and warnings are errors. Node 26 is needed only for the docs site.

CI job `build-test`, on ubuntu-latest and windows-latest, in `.github/workflows/ci.yml`:

```bash
dotnet restore Rag.NET.slnx
dotnet build Rag.NET.slnx --no-restore -c Release
# fast tier: every test project without RequiresDocker, one at a time, with coverage on:
export TestingPlatformCommandLineArguments="--coverage --coverage-output-format cobertura --coverage-settings $PWD/coverage.config.xml"
dotnet test tests/Rag.NET.Storage.Sqlite.Tests --no-build -c Release --verbosity normal
# ubuntu-latest leg only, Docker needed: the Docker tier runs the same command over every
# project with RequiresDocker true and without RequiresLlm
```

CI picks the projects with `grep -l` over `tests/*/*.csproj` and also checks that fast and Docker
together cover every project that references `Microsoft.NET.Test.Sdk`. Each test project must be
in a tier, and must keep that reference. The check counts `RequiresLlm` projects as Docker, but the
Docker loop skips them: they run only in `nightly.yml`, not on a PR. The Linux leg then runs the `coverage-gate` action from
`MarcelRoozekrans/ci-baseline` over `tests/*/bin/Release/**/TestResults/*.cobertura.xml`. The gate
is not a command here, so the commands above do not reproduce it.

CI job `pack-validate` on ubuntu-latest, which also pushes the packages to a local feed, twice:

```bash
dotnet tool restore
VERSION=$(dotnet dotnet-gitversion /output json | jq -r '.SemVer')
rm -rf artifacts/packages
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$VERSION"
dotnet test tests/Rag.NET.PackageValidation.Tests -c Release --verbosity normal
```

The `PackageValidation` tests skip when `artifacts/packages` is missing, and check the version
against GitVersion, so always pack first with that version.

CI job `build` in `docs.yml`, ubuntu-latest: `npm ci && npm run build`.

Inner loop, no CI job:

```bash
dotnet run --project tests/Rag.NET.Storage.Sqlite.Tests -f net10.0 -- -class Rag.NET.Tests.Storage.SqliteCostLedgerTests
dotnet run --project benchmarks/Rag.NET.Benchmarks -c Release -- --filter "${FILTER:-*}"
```

Use the native xunit runner above for one class, with `-f` because the project has two targets.
`dotnet test --project tests/Rag.NET.Storage.Sqlite.Tests -c Release --filter "FullyQualifiedName~SqliteCostLedgerTests"` works too: `global.json` selects
the Microsoft.Testing.Platform runner, which applies the filter. On an empty `obj/`, run `dotnet build` before `dotnet test`.

## Conventions and gotchas

- Test tiers are declared in each test `.csproj`: no property is the fast tier, `RequiresDocker` is
  the Docker tier, `RequiresLlm` runs nightly only and never gates, and `RequiresSecrets` is an
  overlay for the nightly `env-gated` job. Those suites call `Assert.Skip` when their variable is
  unset, so a green local run may have skipped them. The native runner prints the skip reason.
- `Rag.NET.PackageValidation.Tests` and `Rag.NET.RepoConventions.Tests` are guards, not feature
  tests. They read the source tree and the produced packages, and fail on drift: package metadata,
  docs, workflows and tiers. Read the failure message before changing the guard.
- Connectors under `src/Rag.NET.DataProviders*` must not write a reserved metadata key by hand into
  `FileHandle.Metadata` or `FileEntry.Metadata`. The collision throws at ingest time. The keys are
  in `ReservedMetadataKeys.All`, and the pipeline adds them itself.
- Benchmark projects, samples and tests are excluded from coverage in `coverage.config.xml` and
  `codecov.yml`. Keep both in step when a project is added: `codecov.yml` also counts two uploads per
  commit, matching the two `build-test` legs.

<!-- org-core:start sha256=08a88996bc9165f888fc0260607cfd0670cae128c323e16707c6b452ee66222d (generated from docker-nuc scripts/agents-md/org-core-personal.md; do not edit here) -->
## Org rules

These rules hold in every public MarcelRoozekrans repo.

### Commits and releases

- Conventional Commits. release-please reads them on the default branch, and the changelog sections
  in `release-please-config.json` decide which types count. The standard: `feat`, `fix`, `perf`,
  `deps` and `revert` open or update the release PR; `docs`, `test`, `refactor`, `build`, `ci`,
  `chore` and `style` do not, unless the commit is breaking or has a `Release-As:` footer. The
  release happens when that PR merges. A breaking change needs `!` or a `BREAKING CHANGE:` footer.
- A commit body never contains nested parentheses. release-please cannot parse them and silently
  drops the whole commit from the changelog, while the workflow still reports success.
- After merging a commit that should release, check that it appears in the release-please PR.

### Branches and pull requests

- Every change goes through a branch and a pull request. Never push to the default branch directly.
- A PR body says what the diff does, and nothing the diff does not do.
- Fix review findings in the PR under review, including small ones. File an issue only for a finding
  that belongs to other code, needs a design decision, is its own piece of work, or waits on
  something external.

### CI

- The .NET repos call the shared workflows of `MarcelRoozekrans/ci-baseline`, pinned to `@v1`, never
  `@main`.
- Its coverage gate fails the build when line coverage drops below the floor of 80%, and Codecov
  fails a PR that lowers project coverage by more than 1 percentage point. New code comes with
  tests.
- A repo with a lower floor documents the reason and links the issue that raises it.
<!-- org-core:end -->
