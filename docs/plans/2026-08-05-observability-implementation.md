# Observability Implementation Plan (Phases 4.3 and 4.4)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make every part of the pipeline observable — scoped logs, OTel GenAI attributes on the LLM surface, and spans in the nine packages that currently have none.

**Architecture:** The `ActivitySource` moves to `src/Shared/` and is linked into each tracing package, so satellites emit spans without gaining a dependency. A `Rag.NET.Telemetry` satellite registers both meters and the resource attributes. Logging gains scopes, which do not exist today.

**Tech Stack:** .NET 10, `System.Diagnostics.ActivitySource`/`Metrics`, OpenTelemetry SDK (satellite only), xUnit v3.

**Design:** `docs/plans/2026-08-05-observability-design.md`

---

## Measured before planning

**The existing tag surface** — 12 facts across 3 files assert these:

| Tag | Assertions | Fate |
|---|---|---|
| `document.id` | 6 | keep |
| `synthesis.strategy` | 2 | keep — no GenAI equivalent |
| `source.count` | 2 | keep — no GenAI equivalent |
| `chunk.count` | 2 | keep |
| `vector_store` | 1 | **rename** → `vector_store` is snake_case; normalise |
| `top_k` | 1 | **rename** → snake_case; normalise |
| `result.count`, `query.hash`, `parser.type` | 1 each | keep |

**So the `gen_ai.*` work is narrower than "rename everything".** OTel's GenAI conventions cover the
LLM call — model, system, operation, token usage. They have **no concept** of `source.count`,
`synthesis.strategy`, chunking or retrieval. Those stay `ragnet.*`.

Two separate changes, not one:
1. **Add** `gen_ai.*` attributes to the LLM surface (`ragnet.ask`, `CostAccounting`'s counters).
2. **Normalise** the two snake_case outliers (`top_k`, `vector_store`) so `ragnet.*` is internally
   consistent.

**Test files:** `tests/Rag.NET.Tests/Telemetry/{AskTelemetryTests.cs (2), IngestTelemetryTests.cs (7), RetrieveTelemetryTests.cs (3)}`, serialised by `TelemetryCollection.cs`.

**The nine untraced packages:** `VectorStores.{Qdrant,PgVector,Pinecone,Weaviate,Chroma,AzureAISearch}`, `Reranking.{Onnx,Cohere}`, `GraphRag`, `Raptor`, `Graph`, `Security`, `Caching`.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`, `TreatWarningsAsErrors=false`.** MA0051 (≤60-line methods), MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Central Package Management — no `Version` on `PackageReference`.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- A file watcher edits `.csproj` concurrently — `git status` before committing.
- Telemetry tests share a global `ActivitySource`/`Meter` and are **collection-serialised**. New telemetry tests must join `TelemetryCollection` or they will flake.

**Baselines (main, 2026-08-05):** `Rag.NET.Tests` **1169**, `DataProviders.Tests` **71**, `RepoConventions` **37 + 1 skip**, `PackageValidation` **20**, `Evaluation.Tests` **388**. Measure each package suite before touching it.

---

# PART 1 — Phase 4.3: logging

## Task 1: Record what is already true

**Before writing code**, verify and record the measurement, because the phase's scope depends on it:

```bash
grep -rc 'LoggerMessage' src/ --include=*.cs | grep -v ':0' | awk -F: '{s+=$2} END {print "LoggerMessage: " s}'
grep -rn 'BeginScope' src/ --include=*.cs | grep -v obj | wc -l
grep -rnE 'Log(Information|Warning|Error|Debug|Trace|Critical)\(\$"' src/ --include=*.cs | grep -v obj | wc -l
```

Expected: ~140 / **0** / **0**. **If interpolation is not zero, the design is wrong and the phase grows — report it.**

Record the result in the eventual close. No commit needed for this task alone.

## Task 2: Scopes

**Files:** `src/Rag.NET/Pipeline/RagPipeline.cs`, `src/Rag.NET/Ingestion/PipelineIngestor.cs`, `src/Rag.NET/Retrieval/PipelineRetriever.cs`

`BeginScope` appears **zero times**. Add one scope per pipeline operation carrying the identity every log inside would otherwise restate — `document_id` for ingestion, the query hash for retrieval and answering.

**Write the failing test first.** Asserting scope content needs a logger that captures scopes — `FakeLogger` from `Microsoft.Extensions.Diagnostics.Testing` records them; check whether the repo already references it before adding anything.

**Do not add a scope to a hot loop** — one per operation, not one per chunk. If a scope would be created per chunk, say so and leave it out.

## Task 3: Event names and properties

Standardise `[LoggerMessage]` `EventName`s to snake_case, and ensure the properties `features.md`
promises (`document_id`, `chunk_index`, `vector_store`, `strategy`) exist as **log fields**, not
just message-template placeholders.

**This touches ~40 `*Log.cs` files across many packages.** Group commits by package family.
`EventId` values must stay stable — **only names change**; renumbering would break anyone filtering
on ids.

---

# PART 2 — Phase 4.4: tracing and metrics

## Task 4: The shared ActivitySource — everything else depends on this

**Files:**
- Create `src/Shared/RagTelemetrySource.cs` — `internal static class` holding the `ActivitySource` named `"Rag.NET"`, version pinned
- Modify `src/Rag.NET/Telemetry/RagTelemetry.cs` to use it
- Link it into each package that will trace, following the existing precedent:

```xml
<Compile Include="..\Shared\RagTelemetrySource.cs" Link="Telemetry\RagTelemetrySource.cs" />
```

Precedent: `GraphErrorMapping.cs` → Microsoft365, `BertWordPieceTokenization.cs` → both ONNX packages.

**Each assembly gets its own `ActivitySource` instance sharing the name.** That is normal — OTel
matches by name and listens to all of them. **Add a test proving two assemblies' spans both reach a
single `ActivitySource` listener on `"Rag.NET"`**, because the whole nine-package design rests on
it and it is cheap to verify now rather than discover later.

**No package may gain a `ProjectReference` to core or a NuGet dependency from this.** Check each
`.csproj` diff.

## Task 5: The span-naming convention — decide once, write it down

Before instrumenting anything, settle and **document** the convention in the design doc or a
`docs/reference/` page:

- Span names: `ragnet.<area>.<operation>` — e.g. `ragnet.vectorstore.search`, `ragnet.rerank`
- Tag names: **dotted**, lower-case (`document.id`, `chunk.count`)
- Which tags every span carries versus which are area-specific

**Nine packages will follow this. Deciding it per-package produces nine conventions.**

## Task 6: `gen_ai.*` on the LLM surface

**Files:** `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`, `src/Rag.NET/Resilience/CostAccounting.cs`

**Determine the OTel GenAI semconv version to target and pin it in a comment** — these conventions
are **experimental** and will move. A future reader must be able to tell which revision was
implemented.

Add `gen_ai.*` for what it genuinely covers — operation name, system, request model, token usage.
**Do not invent `gen_ai.*` names for RAG concepts it has no equivalent for**: `source.count` and
`synthesis.strategy` stay `ragnet.*`. Inventing a plausible-looking `gen_ai.rag.*` would be worse
than keeping ours, because it would look standard and not be.

## Task 7: Normalise the two snake_case outliers

`top_k` → `top.k` (or the convention's choice from Task 5), `vector_store` → `vector.store`.

**Update the asserting tests as a stated consequence** — `RetrieveTelemetryTests` and
`IngestTelemetryTests`. Nothing is published, so no compatibility shim is needed; say so in the
commit rather than leaving it implied.

## Task 8: Instrument the nine packages

One commit per package or per family — say which grouping you chose.

**Vector stores** (6) — `ragnet.vectorstore.<operation>` around search/upsert/delete, tagged with the store name and result count.
**Rerankers** (2) — `ragnet.rerank`, tagged with model and candidate count.
**GraphRAG, RAPTOR, Graph** — traversal and tree-build operations.
**Security** — guard and sanitiser decisions. **Never put the removed content in a tag.**
**Caching** — hit/miss.

**One test per package** asserting the span exists with its tags. **Join `TelemetryCollection`** —
these tests share global state and will flake otherwise.

**If a package's operation is already inside a core span**, the new span must nest, not replace.
Verify parent/child in at least one test.

## Task 9: `Rag.NET.Telemetry`

**Files:** `src/Rag.NET.Telemetry/` — new packable project taking the OpenTelemetry SDK.

`AddRagNetInstrumentation()` for tracing and metrics, registering:
- the `"Rag.NET"` source
- **both** meters — `"Rag.NET"` **and `"Rag.NET.Evaluation"`**, the one consumers silently miss today
- resource attributes

**Test that both meters are registered.** That omission is the package's whole reason to exist.

Follow the 4.7 satellite pattern: own `Description`, `VerifiedBy`, `InternalsVisibleTo`, its own README (`PackageReadmeTests` will require one), and a central pin for any new package.

## Task 10: Documentation and close

**`docs/reference/opentelemetry.md`** — its metrics table lists **8 of 11** instruments, missing `ragnet.ratelimit.wait.duration`, `ragnet.llm.tokens`, `ragnet.llm.cost`. Fix it, document the new spans, both meters, and the resource attributes.

**Add a guard test asserting the documented instrument list matches `RagTelemetry`.** This table has already drifted once; a doc that disagrees with the code is this repository's most-repeated defect.

**A sample dashboard** — 4.4's description promises one and none exists.

**Close both phases** in `ROADMAP.md` and `MILESTONE.md`, recording:
- 4.3 was **~92% already done**; the phase's honest content was scopes and naming
- the deferral of package-specific spans was **overruled by the owner**, and why: "until evidence demands it" was written 2026-04-04 when the library was far smaller
- the semconv version targeted, and that it is **experimental**
- `ShadowTelemetry` convergence deliberately **not** done — its meter rename would break anyone scraping it

**Do not tick a DoD box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.PackageValidation.Tests
```

Plus every package suite touched. **The deliverable is that a slow query's trace shows which vector store, which reranker and which traversal spent the time — the question that motivated overruling the deferral.**
