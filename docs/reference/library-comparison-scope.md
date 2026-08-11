---
id: library-comparison-scope
title: Library Comparison — Scope
---

# Library Comparison — the Scope Axis

**What each library ships, rather than how well it retrieves.**
[Library Comparison at Defaults](./library-comparison.md) measures quality and cost and says, in
its own closing section, that it does **not** measure "operational maturity, ecosystem, hosting,
security posture". This page is that missing axis, and it exists because the quality table's
headline — four of five entrants within thousandths of each other — is easy to misread as *these
libraries are interchangeable*. They are not. They differ enormously in what they cover, and none
of that difference shows up in an nDCG.

**Read this before the table: this is a reading, not a measurement.** Every figure on the quality
and cost pages comes from a committed harness that an outsider can re-run. Nothing here does. This
page is the same genre as [the defaults page](./library-comparison-defaults.md) — source read at a
pinned version, cited per claim — and it inherits that page's rule: *a value without a citation is
a guess, and none of these are.* Where a cell was not read, it says so rather than being filled in
from memory.

**Date read:** 2026-08-11. Python source read from the installed packages in the committed
`uv.lock` environment (`benchmarks/library-comparison-python`); .NET package status read from the
nuget.org registration API the same day; Rag.NET read from this repository's working tree.

## The scoping line, and why it is the most important thing on this page

The matrix records **what a library ships in the package(s) you get by installing it** — its
first-party surface. That line is the only one that can be read consistently across five libraries,
and it is also **structurally unfair to LangChain and LlamaIndex**, whose whole design is a thin
core plus an enormous partner ecosystem.

- `langchain-core` 1.5.3 is *deliberately* abstractions. Its `document_loaders/` holds `base.py`,
  `blob_loaders.py` and `langsmith.py` — **no concrete file parser at all** — and its
  `vectorstores/` holds `base.py` and `in_memory.py` and nothing else. That is not a gap; that is
  the architecture. The concrete loaders, stores and rerankers live in `langchain-community` and
  the per-provider partner packages, **none of which were read for this page.**
- `llama-index-core` 0.14.23 bundles far more (see the table), and still has a large
  integration ecosystem outside core that was **not read**.
- Semantic Kernel's connectors are separate `Microsoft.SemanticKernel.Connectors.*` packages, read
  here only for release status, not for capability.

So a ✖ in a LangChain cell means **"not in langchain-core 1.5.3"**, and very often the capability
exists one package away. A reader who takes this table as "LangChain cannot do X" has misread it.
Rag.NET, whose capabilities are spread over 70 first-party packages, is flattered by exactly the
same line — its packages are all first-party, so they all count. **The table's shape is partly an
artefact of packaging philosophy, and that is stated here rather than buried.**

## Reading the marks

| Mark | Meaning |
|---|---|
| ✅ | Present in the first-party surface, verified by reading source or package metadata |
| ⚠️ | Present but preview / experimental / abstraction-only — the caveat is in the notes |
| ✖ | **Verified absent** by search over the installed source, not assumed absent |
| — | **Not read.** No claim is made either way |

## The matrix

Rag.NET at this branch; Semantic Kernel at 1.78.0 (the version the quality table ran; 1.79.0 is
current and this repository pins below it — commit `9e8c710`); Python entrants at the pinned
versions of the committed lockfile.

| Capability | Rag.NET | Semantic Kernel | LangChain core | LlamaIndex core | Haystack |
|---|---|---|---|---|---|
| Ingestion pipeline | ✅ | ✖ | ✖ | ✅ | ✅ |
| Document parsers, first-party | ✅ 9 pkgs | ✖ | ✖ **none in core** | ✅ | ✅ 12 |
| Source connectors (SaaS/storage) | ✅ 18 | — | ✖ none in core | — | ✖ none in core |
| Chunking strategies | ✅ 13 | ⚠️ experimental | ✅ separate pkg | ✅ | ✅ |
| Vector stores, first-party | ✅ 6 + 2 | ⚠️ **all preview** | ⚠️ in-memory only | ✅ | ✅ |
| Hybrid / sparse retrieval | ✅ | ⚠️ opt-in iface | ✖ | ✅ | ✅ |
| Reranking | ✅ 2 pkgs | ✖ | ⚠️ abstract only | ✅ 4 | ✅ 3 |
| Query transformation | ✅ HyDE, multi-query | ✖ | ✖ | ✅ HyDE | ✅ expander |
| Hierarchical summary index (RAPTOR) | ✅ | ✖ | ✖ | ✖ | ✖ |
| Graph-community RAG (Leiden) | ✅ | ✖ | ✖ | ⚠️ graph, no communities | ✖ |
| Multi-pass answer engines | ✅ 4 | ✖ | ✖ | ✅ | ✅ |
| Conversational memory | ✅ | — | ✅ types | ✅ | — |
| RAG evaluation | ✅ 2 pkgs | ✖ | ✖ | ✅ | ✅ |
| IR metrics in-library | ✅ | ✖ | ✖ | ✅ | ✅ |
| Caching | ✅ | — | ✅ | ✅ | ✅ |
| Observability | ✅ OTel | — | ✅ tracers | ✅ | ✅ |
| Resilience policies | ✅ | — | ✅ rate limiters | ✅ | — |
| Prompt-injection defence | ✅ | — | ✖ | ✖ | ✖ |
| Serving surface | ✅ REST/gRPC/MCP/CLI | — | ✖ | ⚠️ CLI | ✖ |
| **Agent orchestration** | **✖** | **✅** | ⚠️ types only | **✅** | **✅** |
| **Tool / function calling** | **✖** | **✅** | **✅** | **✅** | **✅** |

### The two rows Rag.NET loses, stated plainly

The bottom two rows are the ones a reader should look at first, because they are the only rows
where Rag.NET is the ✖ and almost everyone else is the ✅.

- **Rag.NET has no agent orchestration and no tool-calling surface.** A search of `src/` for an
  agent abstraction, planner or tool-invocation loop returns nothing. Semantic Kernel ships
  `Microsoft.SemanticKernel.Agents.Core` at **1.79.0 stable**; `llama_index/core/agent/`,
  `llama_index/core/tools/` and `llama_index/core/workflow/` are all in LlamaIndex core;
  `haystack/components/agents/` and `haystack/tools/` are in Haystack core. LangChain core carries
  the agent *data types* (`agents.py`: `AgentAction`, `AgentStep`, `AgentFinish`) but not a
  runtime — its executor is outside core.
- **This is a real scope difference, not a packaging artefact.** Rag.NET is a retrieval pipeline.
  If the job is "an agent that decides when to search", Semantic Kernel is the .NET library for
  that and Rag.NET is not — and the two compose rather than compete, since Rag.NET's retrieval sits
  behind `Microsoft.Extensions.AI` abstractions that SK also consumes.

## Findings this page exists to record

**1. Every Semantic Kernel vector-store connector checked has never shipped a stable version.**
The defaults page recorded this for the InMemory connector alone ("no stable release exists"). It
generalises. Read from the nuget.org registration API on 2026-08-11:

| Package | Latest version |
|---|---|
| `Microsoft.SemanticKernel.Connectors.InMemory` | 1.74.0-preview |
| `Microsoft.SemanticKernel.Connectors.AzureAISearch` | 1.74.0-preview |
| `Microsoft.SemanticKernel.Connectors.Qdrant` | 1.74.0-preview |
| `Microsoft.SemanticKernel.Connectors.Pinecone` | 1.74.0-preview |
| `Microsoft.SemanticKernel.Connectors.Weaviate` | 1.74.0-preview |
| `Microsoft.SemanticKernel.Connectors.Postgres` | **1.51.0-preview** |

Six for six, while `Microsoft.SemanticKernel` itself is stable at 1.79.0. Postgres is stranded 23
minor versions behind its siblings. **This is the single largest scope difference between the two
.NET entrants** and it is invisible in the quality table, where SK scored identically to the
control on SciFact: a library can retrieve exactly as well as another and still have no stable
package to do it with.

**2. RAPTOR is absent from all three Python cores** — `grep -rilI raptor --include=*.py` over
`llama_index`, `haystack` and `langchain_core` returns nothing. Recursive-summary tree indexing is
a first-party Rag.NET package (`Rag.NET.Raptor`) and, on this scoping line, nobody else's.

**3. LlamaIndex has graph indexing but not community-detection GraphRAG.**
`llama_index/core/indices/property_graph/` and `.../knowledge_graph/` exist; a search for
`leiden`/`graphrag` across all `.py` in `llama_index` returns nothing. The distinction matters:
graph *indexing* answers entity questions, whereas the Leiden community-report map-reduce in
`Rag.NET.GraphRag` is what answers corpus-wide questions. Marked ⚠️ rather than ✅ or ✖ for exactly
that reason.

**4. Haystack ships IR metrics in core, and this repository's own metrics do not come from it.**
`haystack/components/evaluators/` contains `document_ndcg.py`, `document_mrr.py`,
`document_map.py` and `document_recall.py`. Every published figure in this repository is scored by
`IrMetrics` instead — the control check the quality page states before its table. Recorded because
a reader might reasonably ask why a comparison that runs Haystack does not use Haystack's nDCG:
because then one entrant's own code would compute the metric, which is the thing that page exists
to prevent.

**5. LangChain core ships no concrete document loader and one in-process vector store.**
`langchain_core/document_loaders/` is `base.py` + `blob_loaders.py` + `langsmith.py`;
`langchain_core/vectorstores/` is `base.py` + `in_memory.py`; `cross_encoders.py` is a single
`BaseCrossEncoder(ABC)`; `documents/compressor.py` is a single `BaseDocumentCompressor(ABC)`. This
is the architecture working as intended and is **not** a criticism — it is recorded so the ✖ marks
in LangChain's column are read as "core is abstractions" rather than "LangChain cannot".

**6. LlamaIndex core did not import on the pinned interpreter.** `import llama_index.core` raises
`AssertionError` inside pydantic's `eval_type_backport` on CPython 3.14 with the pydantic resolved
into the environment on 2026-08-11. The readings above were taken from the source files on disk,
which is unaffected. Recorded because it means the environment that produced the published Python
quality figures no longer re-resolves to a working LlamaIndex entrant without a pydantic pin —
a reproducibility debt on [the quality page](./library-comparison.md), not on this one.

## What this page does not measure

- **Not quality, and not cost.** Those are measured, and they are on
  [Library Comparison at Defaults](./library-comparison.md). A ✅ here says a capability exists,
  never that it is good. `Rag.NET.Raptor` being the only RAPTOR in the table is a scope fact; what
  RAPTOR does to nDCG on any corpus is unmeasured by this repository for every entrant including
  Rag.NET.
- **Not the ecosystem.** The single biggest limitation. LangChain and LlamaIndex have hundreds of
  integration packages that this page does not count, because counting them consistently across
  five libraries is a project rather than a page. On any "how many vector stores can I use"
  question, both Python libraries beat every .NET entrant by an order of magnitude, and this table
  does not show that.
- **Not maturity, adoption or support.** Package counts do not distinguish a load-bearing package
  from a thin one, and nothing here counts downloads, contributors or issue latency. Rag.NET's 70
  first-party packages against LangChain core's deliberate minimalism is a difference in packaging
  philosophy at least as much as a difference in capability.
- **Not depth within a row.** "Reranking ✅" covers Rag.NET's two packages, LlamaIndex's four
  postprocessors and Haystack's three rankers without saying they are equivalent. They are not.
- **A dated reading of pinned versions.** Same caveat the quality page carries, and it bites
  harder here: scope changes with every release, and SK went from 1.78.0 to 1.79.0 during the life
  of the quality table.

## Reproducing the readings

The Python cells are `ls` and `grep` over the committed lockfile's environment:

```bash
cd benchmarks/library-comparison-python
uv sync
P=.venv/lib/python3.14/site-packages
ls $P/langchain_core/document_loaders $P/langchain_core/vectorstores
ls $P/llama_index/core $P/haystack/components
grep -rilI "raptor" --include="*.py" $P/llama_index $P/haystack $P/langchain_core   # expect: no output
grep -rnI "leiden\|graphrag" --include="*.py" $P/llama_index                        # expect: no output
```

`--include="*.py"` and `-I` are not optional: without them the bundled NLTK `punkt_tab` caches
match "raptor" and "graphrag" as **binary** hits, and a reader repeating a naive grep will
conclude both features exist in LlamaIndex. That false positive was hit while writing this page.

The Semantic Kernel package-status table is the nuget.org registration API, which needs no
credentials and no repository access:

```bash
curl -s "https://api.nuget.org/v3/registration5-gz-semver2/microsoft.semantickernel.connectors.qdrant/index.json" \
  | gunzip | python3 -c "import json,sys; print(json.load(sys.stdin)['items'][-1]['upper'])"
```

Rag.NET's own column is guarded by `ScopeMatrixClaimTests` in
`tests/Rag.NET.RepoConventions.Tests`, which re-reads this page's matrix and fails if a ✅ names a
capability whose package is not under `src/`, or if either ✖ row acquires the package it claims
does not exist. The comparator columns cannot be guarded that way — nothing in this repository
builds them — which is the honest limit of the "properly" in this page's brief.
