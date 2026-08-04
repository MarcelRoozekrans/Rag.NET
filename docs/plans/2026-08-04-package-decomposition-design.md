# Package Decomposition, Consolidation & Per-Package READMEs — Design

**Date:** 2026-08-04
**Milestone:** 4 — Release Readiness
**Status:** approved (design)

## 0. Why this phase exists, and why now

Phase 4.1 packaged 70 projects and deliberately published none of them. The question that
opened this phase was "70 packages is a lot — can we pack them more cleverly?", and the stated
cost was **consumer confusion**: users cannot tell what to install, cannot choose among
near-identical options, and cannot read the nuget.org listing.

Measuring that question produced a different and larger finding, recorded in §1.

**The window is now.** Nothing is published and no package ID is reserved, so every rename and
every extraction here costs a `git mv`. After Phase 6.3 the same changes are permanent breaking
renames requiring `[Obsolete]` shims and deprecation notices. **This phase must land before 6.3.**

## 1. The audit: the satellites are pluggable, the core is not

The library's granularity claim holds for the 63 satellite packages. It does not hold for the core.

`Rag.NET` core is 103 files and 10,097 lines, and its **transitive closure is 49 packages**.
Attributing every one of them to the direct dependency that pulls it:

| Cluster | Transitive packages | Gated behind | Always on? |
|---|---|---|---|
| `Microsoft.Extensions.Resilience` (Polly, Diagnostics, Telemetry, Compliance, Options, Hosting) | **15** | `ConfigureResilience()` | no |
| `Microsoft.Extensions.Caching.Hybrid` (HybridCache, Caching.Memory/Abstractions) | **7** | `UseCaching()` | no |
| `Microsoft.Data.Sqlite` + `SQLitePCLRaw` (native binaries per RID) | **6** | `UseSqlitePersistence()` | no |
| `Microsoft.ML.Tokenizers` + `Data.Cl100kBase` (multi-MB vocabulary) | **3** | `UseConversationMemory()`, cost accounting | no |
| `Rag.NET.Abstractions` | 8 | — | yes |
| analyzers (6) | — | `PrivateAssets=all`, never shipped | — |

**31 of the 43 packages a consumer downloads — 72% — exist to serve features the user must
explicitly opt into.** A user on Qdrant who never calls `UseSqlitePersistence()` still ships a
SQLite engine with native binaries for every RID.

The `.nupkg` sizes were never the problem: core is 160 KB and the median package is 32 KB. The
weight is entirely transitive, which is why the catalogue size was the visible symptom and the
dependency closure was the actual defect.

**The library already knows the right pattern.** `UsePgVector()` lives in
`Rag.NET.VectorStores.PgVector`, not in core. `ConfigureResilience()`, `UseCaching()` and
`UseSqlitePersistence()` break that convention by living in core while their dependencies are
mandatory. This phase makes the core consistent with the rest of the library.

## 2. Core decomposition — four extractions

Each cluster moves to a satellite package holding both the implementation **and** its builder
method, exactly as `PgVectorBuilderExtensions` does today.

| New package | Moves | Removes from every consumer |
|---|---|---|
| `Rag.NET.Resilience` | `ResilientVectorStore`, `ResilientSparseVectorStore`, `ResilientEmbeddingGenerator`, `ConfigureResilience` | 15 |
| `Rag.NET.Caching` | `EmbeddingCacheBehavior`, `ResultCacheBehavior`, `UseCaching` | 7 |
| `Rag.NET.Storage.Sqlite` | the 7 `Sqlite*` stores, `SqliteStoreHelper`, `UseSqlitePersistence`, `UseContentHashRecordManager` | 6 |
| *(conditional)* tokenizer users | `ConversationMemoryPipeline`, `CostAccounting` token counting | 3 |

**Expected result: a consumer's download falls from 43 packages to roughly 15.**

The fourth extraction is **conditional on measurement**, not assumed. Token counting may be
reachable from the always-on path in a way the other three are not; if it is, the honest outcome
is to leave it in core and record why, rather than force a split that needs a seam to work.

### The risk that decides this design

`ResultCacheBehavior` and `EmbeddingCacheBehavior` sit in the **retrieval pipeline**. If either is
registered in the default pipeline composition rather than only under `UseCaching()`, extraction
changes default behaviour — and this phase does not change behaviour.

**Planning must verify registration for each of the four clusters before moving anything.** Where a
type proves to be on the always-on path, it stays in core and the finding is recorded. The
extraction count is an outcome of that check, not an input to it.

## 3. Satellite consolidation — three merges

Merge only where **a user would never want one without the other** *and* **the dependency closure
is already identical**. Both tests, not either — the first alone would merge unrelated things, and
the second alone would merge `Graph` with `Security` because they happen to share SQLite.

| Merge | → | Shared dependency |
|---|---|---|
| `Parsers.Word` + `.Excel` + `.PowerPoint` | `Rag.NET.Parsers.Office` | `DocumentFormat.OpenXml` |
| `DataProviders.Exchange` + `.MicrosoftTeams` + `.OneDrive` + `.SharePoint` | `Rag.NET.DataProviders.Microsoft365` | `Microsoft.Graph`, Kiota, `Azure.Identity` |
| `Chunking` + `.TokenAware` + `.Semantic` | `Rag.NET.Chunking` | `Microsoft.ML.Tokenizers` (Chunking and TokenAware only — pre-merge `.Semantic` declared just `Rag.NET.Abstractions`) |

For the first two merges no consumer gains a dependency: every dependency of the merged package
was already declared by every source. The Chunking merge has one measured exception — pre-merge
`Rag.NET.Chunking.Semantic` declared only `Rag.NET.Abstractions`, so a Semantic-only consumer
gains `Microsoft.ML.Tokenizers` + `Microsoft.ML.Tokenizers.Data.Cl100kBase` as declared
dependencies. The cost in bytes is zero — the core `Rag.NET` package independently ships both
tokenizer packages and semantic chunking is unusable without core — but the honest criterion the
merges actually satisfy is "every dependency of the merged package was already a dependency of
*at least one* source", not "of each source".

### What deliberately does not merge

- **The 8 REST connectors** (Slack, Jira, Notion, Confluence, Asana, Linear, Bitbucket, Zendesk)
  share only the tiny `ZeroAlloc.Rest`. Wanting Slack without Jira is the normal case, and
  individual discoverability on nuget.org is the entire value of the split.
- **`Graph` + `Security`** share SQLite; unrelated concepts.
- **`DataProviders.Web` + `Parsers.Html`** share AngleSharp; unrelated concepts.
- **`Embeddings.Onnx` + `Reranking.Onnx`** share the ONNX runtime, but local embeddings with a
  hosted reranker is a real configuration, so the split is a genuine choice.
- **`Chunking.CSharp`** isolates Roslyn.

## 4. The Templates dependency leak — move code, not packages

`Chunking.Templates` holds six template strategies. Two of them ship document *parsers* that pull
`MimeKit`, `CsvHelper` and `ClosedXML` — so a user wanting the **Book** template, which needs
nothing, currently installs an email stack and a spreadsheet library.

`EmailTemplateDocumentParser` moves to `Rag.NET.Parsers.Email` (which already carries MimeKit);
`QAPairsDocumentParser` moves to a parser home. `Chunking.Templates` is left dependency-free.
**No package is added or removed.**

Planning must verify the dependency direction does not invert — if a parser package would need
template types, keep a thin seam instead.

## 5. Per-package READMEs — the deliverable, and how it stays true

All 70 packages currently ship the same repo-wide README, so every nuget.org page shows the whole
project. Each package gets its own, with a fixed structure:

1. **What it is** — one sentence.
2. **Install** — the `dotnet add package <exact id>` line.
3. **Setup** — the builder call, in context, with the `using` directives.
4. **A working example** — the minimum that does something real.
5. **Link** to the full guide.

### The examples must be verified, not merely written

Sixty-six hand-written examples is sixty-six new homes for this project's dominant defect: docs,
code and tests agreeing with each other and all being wrong. There is currently **no doc-snippet
verification anywhere in the repo**, so this phase builds one.

A `PackageReadmeTests` guard asserts, for every package:

- a README exists, and is **not** the repo README (byte comparison);
- the install line names **that package's exact ID**;
- every public API named in the README's C# fence — builder methods, types — **resolves against
  that package's compiled assembly by reflection**.

The third is the one that matters. It catches the rename, the removed overload and the aspirational
example, which is precisely how the OCR instructions in `features.md` came to tell users to do
something impossible. Reflection is chosen over full compilation because it is cheap enough to run
in the gating tier; full compilation of every snippet is recorded as a possible later strengthening.

## 6. The package chooser

A single documentation page answering "what do I install?", stating plainly what the audit found:
`Rag.NET` brings `Abstractions` transitively, each connector brings the `DataProviders` base, and
the default chunker is already in core. That baseline is **already** transitive — users simply have
no way to know, which is a documentation failure rather than a packaging one.

## 7. Namespaces do not change

Only package **IDs** and project locations change. Every type keeps its namespace, so no consumer's
`using` statements break; a migration is editing `PackageReference` lines. Since nothing is
published, no shims or deprecation notices are needed at all.

## 8. Verification

- `PackabilityTests`, `PackageDescriptionTests` and `ProducedPackageTests` assert package counts.
  Those counts change **deliberately** — each must be updated as a stated decision, not adjusted
  until green.
- A new guard asserting each merged package's dependency closure **equals the union of its
  originals'**, so "no consumer pays more" is mechanically enforced rather than claimed.
- A new guard asserting each extracted package's closure is **absent from core's**, so the
  extraction cannot silently regress by a re-added reference.
- The existing test suites must hold at their baselines: `Rag.NET.Tests` 1318, `RepoConventions` 34,
  `Benchmarks.Quality` 163, `Evaluation` 388, `PackageValidation` 15.
- **A behaviour check**: the default pipeline composition before and after extraction must register
  the same services. This is the guard against §2's central risk.

## 9. Sequencing

The phase is large enough to split, and the parts have different risk profiles:

1. **Decomposition and merges** — project-file and file-move work, verified by closure guards.
2. **READMEs and the chooser** — content work, verified by the reflection guard.

Part 1 changes what consumers download; part 2 changes what they understand. Part 1 should land
first, because the READMEs must describe the final package set — writing 70 READMEs and then
merging ten of the packages would waste the work.

## 10. Out of scope

- **`Rag.NET.Mcp.Tool` is 19 MB** against a 160 KB median. Probably legitimate self-contained tool
  output, but unconfirmed. Recorded as a follow-up; it must be explained before 6.3 publishes it.
- **The repo carries both `ClosedXML` and `DocumentFormat.OpenXml`** — two Excel libraries. Worth a
  look, not in this pass.
- `GenerateDocumentationFile` across `src/`, already owned by Phase 4.2.
- Any change to retrieval, chunking or ingestion **behaviour**. This phase moves code and rewrites
  project files; it does not change what the library does.
