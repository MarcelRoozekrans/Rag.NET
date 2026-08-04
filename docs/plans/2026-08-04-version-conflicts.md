# Phase 4.8 Task 2 — Version conflict resolutions

Central Package Management permits one version per package. The scan
(`PackageReference Include=... Version=...` across `src/`, `tests/`,
`benchmarks/`, `samples/`) found exactly four packages referenced at more than
one version, matching the planning table. Resolved versions below were read
from `obj/project.assets.json` after the Task 1 restore (2026-08-04).

## Decisions

### Microsoft.Data.Sqlite — pin 10.0.10

- `10.*`: `src/Rag.NET.Graph`, `src/Rag.NET.Security`
- `10.0.10`: `src/Rag.NET.Storage.Sqlite`
- `10.*` resolves to **10.0.10** today, and the baseline
  (`2026-08-04-nuspec-baseline.txt`) shows all three packages already ship a
  `>= 10.0.10` floor. Pinning 10.0.10 is a no-op for the shipped contract.
- **Shipped floor moves: no.**

### Microsoft.Extensions.DependencyInjection — pin 10.0.10

- `10.*` (most test projects, `samples/Rag.NET.Sample`), `10.0.10` (four test
  projects), `10.0.5` (`tests/Rag.NET.Embeddings.Onnx.Tests`), `9.*` (three
  test projects, listed under Risk below)
- `10.*` resolves to **10.0.10** today. Tests and samples only; this package
  is not a dependency of any shipped nuspec (only
  `Microsoft.Extensions.DependencyInjection.Abstractions` is, and that is not
  in conflict).
- **Shipped floor moves: no.**

### Microsoft.Extensions.Logging — pin 10.0.10

- `10.*` (`tests/Rag.NET.Chunking.Templates.Tests`,
  `tests/Rag.NET.Security.Tests`, `tests/Rag.NET.Parsers.Vision.Tests`),
  `10.0.10` (`tests/Rag.NET.Tests`)
- `10.*` resolves to **10.0.10** today. Tests only; not a dependency of any
  shipped nuspec (only `Microsoft.Extensions.Logging.Abstractions` is).
- **Shipped floor moves: no.**

### Microsoft.Extensions.AI.OpenAI — pin 10.8.3

- `10.*`: `benchmarks/Rag.NET.Benchmarks.Quality.Hypotheticals`,
  `tests/Rag.NET.Testing`; `9.*`: `samples/Rag.NET.Sample`
- `10.*` resolves to **10.8.3** today. Benchmarks, tests, and samples only;
  not a dependency of any shipped nuspec.
- **Shipped floor moves: no.**

## Baseline observation

`Qdrant.Client` — the package whose float caused this phase — packed at
**1.19.0**, not the 1.18.1 measured at planning time. The floating `1.*`
moved the shipped floor again between planning and execution, demonstrating
the defect live. The pin decision for it belongs to the CPM sweep (Task 3+),
not this note; the baseline records what ships today.

## Risk carried to Task 4

Three test projects currently reference
`Microsoft.Extensions.DependencyInjection` at `9.*` and will move to 10.0.10
in the Task 4 sweep:

- `tests/Rag.NET.VectorStores.Qdrant.Tests`
- `tests/Rag.NET.VectorStores.PgVector.Tests`
- `tests/Rag.NET.VectorStores.AzureAISearch.Tests`

Not tested now — the sweep has not happened. Task 4 must run these three
suites and watch them specifically. If a suite breaks on the 9.x → 10.x move,
`VersionOverride` on that project is the escape hatch, recorded as debt with
this note as its origin — not adopted silently as a default.
