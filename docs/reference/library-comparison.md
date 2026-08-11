---
id: library-comparison
title: Library Comparison
---

# Library Comparison at Defaults

**Five RAG libraries, two BEIR corpora, one pinned embedder, everything else at each library's own
defaults.** Quality measured by Phase 3.14 (2026-08-02); [cost](#cost-retrieval-latency-and-index-construction)
— retrieval latency and index construction, three repeat runs per cell — by Phase 5.1 (2026-08-10).
The configuration every entrant ran at is recorded,
with source citations at pinned versions, in
[the defaults page](./library-comparison-defaults.md) — written *before* any entrant existed, so the
entrants were built to match the page rather than the page written to excuse the entrants. What
Rag.NET's own path scores, against published references, is
[Retrieval Quality](./retrieval-quality.md); this page is the one place that compares it to anyone
else.

**Read the headline before the table: on these corpora, the defaults barely matter.** Four of the
five entrants sit within about 0.001 of each other on SciFact and within about 0.0014 on ArguAna —
differences an order of magnitude smaller than what a single protocol change does to the same
corpora ([below](#the-honest-headline-defaults-barely-matter-here)) — and the one entrant that
stands measurably apart does so for a reason the table can name.

## The control check, stated before the table

Every entrant, Rag.NET included, is scored the same way: the library ranks, the ranking is written
to a **TREC run file**, and the file is read back and scored by the one `IrMetrics` implementation
behind every published number in this repository. No entrant's own code computes a metric, so no
difference between rows can come from evaluation code.

**Rag.NET's control row goes through that same boundary — and reproduces the figures already
published in [Retrieval Quality](./retrieval-quality.md) exactly**: SciFact 0.64593 and ArguAna
0.50432, identical to the parity runs to five decimal places, with identical Recall@10 and MRR@10.
That check is what makes the other rows readable: if writing rankings to a file and scoring the
read-back moved the control's number, every comparator row would be measuring the boundary rather
than the library. It did not move it, on either corpus.

## The table

**The embedder is matched; everything else is each library's default.** Every entrant embeds with
the same pinned `all-MiniLM-L6-v2` ONNX export (revision and SHA-256 pinned in `nightly.yml`,
truncation at 256, mean-pooled excluding padding, L2-normalised) — the one deliberate departure
from pure defaults, because rows produced by different embedding models would measure the models.
Chunking, top-k behaviour, similarity function and store are whatever the library does when the
caller chooses nothing; retrieval depth 10 is a protocol parameter for all entrants, since nDCG@10's
cutoff belongs to the measurement, not to any library. nDCG@10, parity protocol (one chunk per
document wherever the library itself does not chunk):

| Entrant | Version | SciFact | ArguAna |
|---|---|---:|---:|
| **Rag.NET** (control) | this repository | 0.64593 | 0.50432 |
| Semantic Kernel | 1.78.0 (InMemory connector 1.74.0-preview, MEVD 10.1.0) | 0.64593 | 0.50306–0.50399 |
| LangChain | langchain-core 1.5.3, text-splitters 1.1.2 | **0.64613** | **0.50450** |
| LlamaIndex | llama-index-core 0.14.23 | 0.64508 | **0.50450** |
| Haystack | haystack-ai 3.0.0 | 0.62757 | 0.49715 |

**LangChain scores highest on SciFact, and LangChain and LlamaIndex tie highest on ArguAna.**
Rag.NET's control row is not the top of either column, and this table publishes that plainly —
though the next section says why none of the non-Haystack differences supports a ranking at all.

**Semantic Kernel's ArguAna cell is a range because it is the one figure in the table that is not
reproducible run to run on one machine** — three runs measured 0.50306, 0.50321 and 0.50399, and
[below](#the-honest-headline-defaults-barely-matter-here) explains why that is a finding about SK's
defaults on this corpus, not a measurement error. Every other cell reproduces to five decimals on
the measuring machine.

FiQA is **unrun for every entrant** — recorded as NEVER RUN in `BeirReproduction` and
`BeirRunBudget` with a derived cost of roughly an hour per entrant (the corpus embedding dominates;
FiQA's .NET parity leg measured 1 h 11 m for comparable work). An empty entry is a different state
from an absent one: the day somebody pays for a run, its figure has a place waiting.

(LlamaIndex's SciFact 0.64508 happens to equal MTEB's published figure for this model to five
decimal places. That is a coincidence of rounding, not a reproduction of it — the two numbers come
from different protocols.)

## The honest headline: defaults barely matter here

Everything sits within ~0.02, and everything except Haystack within thousandths. The reason is
mechanical: **most libraries' default chunk sizes exceed most of these documents**, so their
"chunking" defaults barely chunk.

| Entrant | Default chunk unit | SciFact units (5,183 docs) | ArguAna units (8,674 docs) |
|---|---|---:|---:|
| Rag.NET (control) / Semantic Kernel | one per document on this protocol | 5,183 | 8,674 |
| LangChain | 4000 characters | 5,205 (max 3 from one doc) | 8,699 |
| LlamaIndex | 1024 cl100k tokens | 5,196 (max 2 from one doc) | 8,679 |
| **Haystack** | **200 words** | **8,042** (max 8) | **11,342** (max 6) |

LangChain's 4000-character and LlamaIndex's 1024-token defaults leave nearly every SciFact abstract
and ArguAna argument as a single unit — a handful of documents split at all. At that point four of
the five rows are embedding nearly identical text through the same model, and the residue is
tie-ordering: Semantic Kernel's ArguAna row retrieves **the same documents** as the control
(Recall@10 identical at 0.79161 in every run) and differs only in how exact ties order.

**That row is also the table's most interesting single finding: at its defaults on this corpus,
Semantic Kernel's figure is not reproducible run to run on one machine.** Three runs measured
nDCG@10 0.50306, 0.50321 and 0.50399 (MRR@10 0.41339, 0.41361, 0.41471) with Recall@10 at 0.79161
in all three — identical document sets, only tie ordering moving between runs. ArguAna makes it
visible because 1,298 of its 1,406 queries are byte-identical to their own corpus document, so
exactly-equal cosine scores are everywhere, and SK's InMemory connector does not order exact ties
the same way across processes. The entrant deliberately applies no tie-break of its own — re-sorting
SK's output would stop measuring Semantic Kernel and start measuring our re-sort — so the
nondeterminism is a true property of the library at its defaults, published as the observed spread
rather than hidden behind one run's figure (all three are pinned in `BeirReproduction`). The
SK−control delta is therefore not a fixed quantity: across the three runs it is −0.00126, −0.00111
and −0.00033 — within the row's own run-to-run spread, and tie-ordering rather than a retrieval
difference either way. The control does not move because `DocumentRanking` breaks exact ties by
ordinal document id; the Python rows do not move because their libraries' orderings are
deterministic functions of insertion order (LangChain sorts with numpy's `argsort` over the store's
fixed order, LlamaIndex's top-k heap breaks exact-score ties by node id, Haystack's stable sort
preserves store order) and the protocol's chunk-to-document pooling then applies the control's
ordinal tie-break to the pooled scores (`doc_ranking.top_documents`).

**Haystack is the only entrant whose default actually chunks these corpora — 200 words, no overlap,
1.6×–1.3× units per document — and the only one measurably lower** (−0.018 on SciFact, −0.007 on
ArguAna against the control). That is consistent with what
[Retrieval Quality](./retrieval-quality.md#what-chunking-does-to-the-numbers-and-it-goes-both-ways)
measured about chunking these corpora with Rag.NET's own defaults, and it is a property of the
default meeting these documents, not a defect in Haystack.

**No ranking among the other four is supported.** Their spreads — 0.00105 on SciFact, at most
0.00144 on ArguAna (taking SK's lowest observed run) — are an order of magnitude smaller than a
single row's run-to-run movement above, and than the deltas Phases 3.12–3.16 measured between
*protocols* on the very same corpora (+0.031, −0.029, −0.015), and smaller than what a handful of
near-ties resolving differently on another CPU can move
([`BeirReproduction`'s ±0.005 reasoning](./retrieval-quality.md#running-it-yourself)). Those four
rows are **not separable** on this measurement. The finding is the flatness itself: when documents
fit inside every default chunk size, the decisions libraries make on your behalf mostly cancel out
of a dense-retrieval benchmark — which also means this table would look different on a corpus of
long documents, where the defaults would actually bite.

## What each entrant's defaults are, and where they forced a choice

Full citations at pinned versions on [the defaults page](./library-comparison-defaults.md). The
short version, with each row's forced substitutions stated:

- **Rag.NET (control):** the parity protocol — one chunk per document — because the control's job
  is to reproduce the published figures through the run-file boundary, not to exercise Rag.NET's
  chunker. What Rag.NET's own default chunking (`RecursiveChunkingStrategy`, 512 characters / 50
  overlap) does to these corpora is the **real** leg of
  [Retrieval Quality](./retrieval-quality.md) (+0.031 SciFact, −0.029 ArguAna against parity —
  swings larger than anything separating the entrants here). Default `TopK = 5`; retrieval at
  depth 10 is the protocol's.
- **Semantic Kernel 1.78.0:** **no default chunker exists** — SK has no ingestion pipeline, and its
  one splitting utility (`TextChunker`) is `[Experimental]` and takes no default size — so SK's
  default is *no chunking*, which means **its row is the parity protocol by construction**. That is
  why it scores identically to the control on SciFact: same texts, same vectors, same cosine, and
  the rankings held no ties that mattered. No default top-k at the vector-store API (`top` is
  required); the in-process InMemory connector has never shipped a stable version
  (1.74.0-preview). No default embedder.
- **LangChain (core 1.5.3):** `RecursiveCharacterTextSplitter` at 4000 characters / 200 overlap;
  `k = 4`; `InMemoryVectorStore`, cosine. No default embedder in core; the companion
  `langchain-openai` defaults to `text-embedding-ada-002`.
- **LlamaIndex (core 0.14.23):** `SentenceSplitter` at 1024 cl100k tokens / 200 overlap;
  **`similarity_top_k = 2`** — at its own default depth it would answer nDCG@10 with a two-deep
  ranking, the strongest argument for depth being a protocol parameter; `SimpleVectorStore`,
  cosine. Its default embedder is `OpenAIEmbedding()` (`text-embedding-ada-002`), which
  **validates an API key at resolution time — LlamaIndex will not run offline at its true
  defaults.**
- **Haystack 3.0.0:** `DocumentSplitter` at 200 words / 0 overlap; `top_k = 10` (the only default
  equal to the metric's cutoff); `InMemoryDocumentStore` under its default **dot product**
  similarity — the one non-cosine default in the table, coinciding with cosine here only because
  the pinned vectors are unit-length. A reader generalising this row to an un-normalised embedder
  would be measuring the similarity function too. Haystack 2.x's default embedder was the pinned
  model itself via sentence-transformers; 3.0.0 removed those embedders from core, leaving OpenAI
  (`text-embedding-ada-002`) the closest thing to a default.

Each library's own default embedder is published even though none was used, because "this library
would otherwise have used X" is what a reader needs to interpret the row — and all of them would
otherwise have used `text-embedding-ada-002`, so **none of the Python entrants runs offline at its
true defaults.** The pinned local embedder is the same forced substitution every entrant got.

**Kernel Memory was dropped, and the drop is the finding.** Its NuGet packages are marked legacy
("no longer maintained"), `0.98.250508.3` (2025-05-09) is the final release, and the repository's
own README calls it "an archived research project". Publishing a number against a project its own
authors archived invites the fair objection that the table picked something that could not answer
back, so the row was never written and **no number is attached**. Two facts from reading its source
stay recorded on [the defaults page](./library-comparison-defaults.md): its default pipeline chunks
at 1000 cl100k tokens / 100 overlap, and its own validation refuses that default against a
256-token embedder — the row could only ever have run at a size KM's code forced.

## The tokenizer finding: the two ecosystems disagree on accented text

**Anyone comparing this repository's BEIR figures against numbers from the Python stack needs this
paragraph.** Proving the Python-side embedder identical to `OnnxEmbeddingGenerator`
(`identity_check.py`, a six-string battery: prose, punctuation, accents, CJK, embedded whitespace,
a truncating text) found exactly one real divergence, and it is not in either model file:

- HuggingFace `tokenizers`' `BertNormalizer` at its default (`strip_accents=None`) **strips accents
  when lowercasing** — reference-BERT behaviour for uncased models.
- `Microsoft.ML.Tokenizers`' `BertTokenizer` at default `BertOptions` — the pipeline behind every
  published figure in this repository — **does not strip accents**, so WordPiece maps `müllerian`
  to `[UNK]` where the HF path finds `mull`-pieces.

On `"anti-Müllerian hormone. It’s café naïveté."` the two pipelines produced vectors **0.166 apart**
(max-abs, over unit vectors) — from the same model file, the same weights, the same text. The
Python harness pins `strip_accents=False` to match the .NET ground truth; after that pin, **all six
battery strings are bitwise identical: 384/384 floats equal, max |diff| = 0.0** (measured
2026-08-02, onnxruntime 1.28.0 against the .NET CPU ONNX Runtime). The divergence was found and
fixed **before any entrant ran**, so no number above contains it — but a comparison built without
this check would, silently, on any corpus with accented text.

## Cost: retrieval latency and index construction

Measured on one machine: Windows 11 (10.0.26200), Intel Core i9-12900HK (14C/20T), 64 GB, .NET
10.0.302, CPU ONNX Runtime, CPython 3.14.5. Three repeat runs of every cell, every run gated.

**Every figure is a range, not a number, and that is the point.** No cost figure here comes from a
single run. `CostReproducibility` reads the repeats and publishes the spread — smallest run,
largest run, and their ratio — because a lone number picked from runs that disagreed is a claim the
data does not make. This is not a formality: an earlier version of this harness had disk reads
inside the timed spans, and identical runs differed by **23×** on OS page-cache state alone, with
every single-run validation passing. Indexing and p50 additionally hard-fail above ×3.

**The `ragnet-control` row got 4–5× faster on 2026-08-11**, and the table below is the
post-optimisation measurement. Phase 5.1's first published figures were what prompted looking:
`SearchAsync` was allocating a list sized to **the whole corpus on every query** — 901 KB at FiQA,
past the Large Object Heap threshold — and sorting it to take ten, while the scoring kernel
recomputed two constant norms per candidate in a scalar loop. A bounded top-k selector, hoisted
norms and a vectorised dot product fixed both, with every pinned nDCG figure on this page verified
unmoved. Details in [ROADMAP Phase 5.1.1](https://github.com/MarcelRoozekrans/Rag.NET/blob/main/docs/planning/ROADMAP.md).

### Query latency, per retrieval call — comparable across ecosystems

| Dataset | Entrant | p50 | p99 *(reported, never gated)* |
|---|---|---|---|
| SciFact | **`ragnet-control`** | **0.3–0.4 ms** | 0.5–0.9 ms |
| SciFact | `semantic-kernel-1.78.0` | 0.9–1.5 ms | 1.8–3.4 ms |
| SciFact | `langchain-core-1.5.3` | 54.0–56.1 ms (×1.04) | 60.6–61.3 ms (×1.01) |
| SciFact | `llama-index-core-0.14.23` | 65.7–68.6 ms (×1.04) | 73.9–78.6 ms (×1.06) |
| SciFact | `haystack-ai-3.0.0` | 78.9–82.4 ms (×1.04) | 89.4–124.9 ms (×1.40) |
| ArguAna | **`ragnet-control`** | **0.9–1.1 ms** | 1.4–3.8 ms |
| ArguAna | `semantic-kernel-1.78.0` | 2.0–4.4 ms | 10.8–18.3 ms |
| ArguAna | `langchain-core-1.5.3` | 88.8–92.7 ms (×1.04) | 97.3–106.5 ms (×1.09) |
| ArguAna | `llama-index-core-0.14.23` | 106.4–117.2 ms (×1.10) | 114.7–133.1 ms (×1.16) |
| ArguAna | `haystack-ai-3.0.0` | 107.8–118.1 ms (×1.10) | 123.3–143.8 ms (×1.17) |
| FiQA | **`ragnet-control`** | **7.9–10.0 ms** | 13.8–27.0 ms |
| FiQA | `semantic-kernel-1.78.0` | 21.4–23.6 ms | 33.7–42.1 ms |

**Rag.NET is now the fastest entrant on all three corpora**, 2–3× ahead of Semantic Kernel and two
orders of magnitude ahead of the Python defaults. Read the two caveats below before quoting either
of those, because both change what the numbers mean.

**The .NET rows come from a different session than the Python rows, and their ranges say so.** The
Python entrants were measured 2026-08-10; the two .NET entrants were re-measured 2026-08-11 after
the optimisation, in **two** separate idle sessions of three gated rounds each, and their columns
publish the union across both — six runs, not three, which is why those ranges are wider than the
Python ones rather than tighter. Strictly this page's own "one machine, one session" rule wants a
single sweep of all five, and that re-sweep is still owed.

**Semantic Kernel is what makes that tolerable, because it is the control for the control.** Its
code did not change between the two sessions, so whatever its rows moved is session variance and
nothing else: 0.7–1.2 → 0.9–1.5 ms on SciFact, 1.9–2.3 → 2.0–4.4 on ArguAna, 21.6–22.6 → 21.4–23.6
on FiQA. That is roughly ±20%, at the same time as the control moved **4–5×**. A cross-session
comparison could not manufacture a change that size, and the Python gap is two orders of magnitude
— far outside anything session state has ever been worth here.

FiQA has no Python rows: no Python entrant has ever run that corpus, so its vector cache is cold,
and a cold entrant would pay 57,638 documents of embedding no other row paid.

**The caveat that must travel with this table, or it misleads.** This compares **default in-memory
stores**, and for the Python entrants the default is a reference implementation nobody runs in
production — LangChain's `InMemoryVectorStore` and LlamaIndex's `SimpleVectorStore` scan candidates
in Python-level loops. *"LangChain is 150× slower"* is **false**; *"LangChain's default in-memory
store is 150× slower than Rag.NET's default in-memory store"* is what was measured. The
"at their defaults" protocol is what makes the row meaningful and is also exactly what makes the
unqualified claim wrong.

The multiplier is also a moving target and should be read as one. It was ~40× when this section was
first published; optimising *our* side of the ratio pushed the same comparison past 150× with
nothing changing in LangChain whatsoever. A number that moves that far on one side's internals was
never a fact about either library — point all five entrants at a real Qdrant or pgvector and the
figure becomes the store's, not the library's.

**p99 is reported and deliberately never gated.** At these query counts it rides on one to three
tail samples, so it moves for reasons a defect-catching bar cannot distinguish from noise: FiQA's
control p99 spans 13.8–27.0 ms while its p50 spans 7.9–10.0, and an earlier SciFact run measured a
×3.35 p99 spread on a p50 that barely moved — a single 7 ms sample among 300 is enough. It is
published anyway, so an unstable tail is visible rather than quietly dropped.

### Index construction — per ecosystem, **not comparable across them**

**.NET entrants** — units pre-built, so the span is store construction only:

| Dataset | Entrant | Indexing |
|---|---|---|
| SciFact | `ragnet-control` | 0.01–0.02 s |
| SciFact | `semantic-kernel-1.78.0` | 0.02–0.03 s |
| ArguAna | `ragnet-control` | 0.02–0.04 s |
| ArguAna | `semantic-kernel-1.78.0` | 0.05–0.11 s |
| FiQA | `ragnet-control` | 0.11–0.19 s |
| FiQA | `semantic-kernel-1.78.0` | 0.18–0.23 s |

**The control's index construction got slower, and that is the trade, not a regression.** FiQA moved
from 0.09 s before the optimisation to 0.11–0.19 s after: each vector's norm is now computed once
on write instead of once per candidate per query. Roughly 0.05 s more at index time buys ~10 ms off
every query, over 57,638 documents — worth it after the first few queries and increasingly so
afterwards. Part of the increase is session variance rather than the change, since Semantic Kernel
moved similarly (0.16–0.18 → 0.18–0.23 s) without any code change at all.

**Python entrants** — the span additionally includes each library's own chunker:

| Dataset | Entrant | Indexing |
|---|---|---|
| SciFact | `langchain-core-1.5.3` | 0.45–0.49 s (×1.07) |
| SciFact | `llama-index-core-0.14.23` | 1.52–1.56 s (×1.02) |
| SciFact | `haystack-ai-3.0.0` | 0.85–0.96 s (×1.13) |
| ArguAna | `langchain-core-1.5.3` | 0.74–0.75 s (×1.02) |
| ArguAna | `llama-index-core-0.14.23` | 2.18–2.25 s (×1.03) |
| ArguAna | `haystack-ai-3.0.0` | 1.30–1.42 s (×1.09) |

**Two tables rather than one, on purpose.** The indexing spans do not bracket the same work: the
Python entrants' spans include each library's own chunker, while the .NET rows receive their units
pre-built — that asymmetry is the parity protocol that makes *quality* comparable — and the Python
harness times a second, warmed build after an untimed rehearsal. Both biases push the same way, and
neither is a library difference, so a cross-ecosystem indexing row would publish a protocol
artefact as a result. Read down each table, never across.

**This is index construction with embedding already paid for**, not "the cost of indexing". Every
vector the run needs is prefetched into memory before any clock starts, on both sides, so embedding
and its disk I/O are excluded by construction — which is what stopped the 23× defect and is also
why these numbers are much smaller than an end-to-end ingest.

## Reproducing it

Everything is pinned and the harness is committed; the corpora, models, vectors and run files are
derived or third-party data and never are.

- **Versions:** every entrant's version is in the table above and cited per default on
  [the defaults page](./library-comparison-defaults.md). The Python environment is a `uv` project
  with **`uv.lock` committed** (`benchmarks/library-comparison-python`, CPython 3.14.5); the .NET
  entrants pin their packages in
  `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/Rag.NET.Benchmarks.Quality.IntegrationTests.csproj`.
- **The boundary:** every row is a TREC run file with self-exclusion and chunk-to-document
  max-pooling already applied on the writer's side, so the file holds the post-exclusion top 10 and
  **an outsider's `trec_eval` scores what `IrMetrics` scores** — no knowledge of this repository
  required. Each line carries a run tag naming the library and exact version that produced it, and
  the scoring tests verify the tag and the self-exclusion on the file's own bytes.
- **Producing the Python rows** (needs `RAGNET_BEIR_CACHE`, `RAGNET_ONNX_EMBED_MODEL`,
  `RAGNET_ONNX_EMBED_VOCAB`, as [Retrieval Quality](./retrieval-quality.md#running-it-yourself)
  documents them):

  ```bash
  cd benchmarks/library-comparison-python
  uv sync
  # Prove the embedder first; a diff invalidates the stage. The battery has two halves:
  uv run python identity_check.py --write-battery "$RAGNET_BEIR_CACHE/identity-battery"
  RAGNET_IDENTITY_BATTERY_DIR="$RAGNET_BEIR_CACHE/identity-battery" \
    dotnet test ../../tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
    --filter "DisplayName~DumpsEachBatteryInputsVector"   # the .NET half: dumps <name>.txt
  uv run python identity_check.py "$RAGNET_BEIR_CACHE/identity-battery"   # all six must be OK
  uv run python run_entrant.py scifact langchain   # then arguana, llamaindex, haystack…
  ```

- **Scoring every row** happens on the .NET side, gated like every expensive case:

  ```bash
  RAGNET_BEIR_LONG_RUNS=1 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
    --filter "DisplayName~ThroughLangChain&DisplayName~scifact"
  ```

  `BeirComparisonControlTests` is the control row, `BeirSemanticKernelDefaultsTests` the SK row,
  `BeirPythonEntrantsTests` the three Python rows. An opted-in case whose run file is missing
  **fails** with the command that produces it rather than skipping into a green summary, and every
  figure on this page is pinned in `BeirReproduction` at ±0.005, so a re-run that drifts fails
  rather than silently republishing.

- **Reproducing the cost tables** needs each cell measured more than once, then gated. Run every
  entrant once per round rather than repeating one entrant back to back: two consecutive runs of
  the same entrant see almost the same machine state, so they agree for the wrong reason and the
  spread stops meaning anything. Then dump:

  ```bash
  for i in 1 2 3; do
    for dataset in scifact arguana; do
      for entrant in langchain llamaindex haystack; do
        uv run python run_entrant.py "$dataset" "$entrant" --run-index $i
      done
    done
    RAGNET_BEIR_LONG_RUNS=1 RAGNET_BEIR_RUN_INDEX=$i \
      dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
      --filter "FullyQualifiedName~BeirComparisonControlTests|FullyQualifiedName~BeirSemanticKernelDefaultsTests"
  done
  RAGNET_COST_MATRIX_RUNS=3 dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests \
    --filter "DisplayName~DumpsTheGatedCostMatrix" --logger "console;verbosity=detailed"
  ```

  The machine must be otherwise idle — every figure is a latency measurement, and a full Release
  rebuild between two runs was on its own worth ×2.2 on indexing.

## What this table does not measure

- **Not end-to-end ingestion throughput, and not memory.** The cost tables above measure retrieval
  latency and index construction with embedding excluded by construction — not the cost of getting
  a document from disk into a store, which is dominated by embedding and parsing on every entrant.
  Allocations per query and AOT startup are .NET-only and stay on [Benchmarks](./benchmarks.md),
  which covers only Rag.NET.
- **Not interpreter or runtime startup.** Excluded on both sides by construction: every span
  brackets a call in an already-warm process. A cold-start comparison would be a different
  measurement and would favour neither ecosystem for the reason this one does.
- **Not production suitability.** Operational maturity, ecosystem, hosting, security posture —
  none of it is in an nDCG. **What each library ships** — the scope axis rather than the quality
  one — is read, cited and tabulated in [Library Comparison — Scope](./library-comparison-scope.md),
  which is a reading rather than a measurement and says so before its table.
- **Not any library's ceiling.** Every entrant would score differently tuned — that is the point of
  a defaults table and also its limit. It measures the decisions a library makes on your behalf
  when you make none, and on corpora whose documents fit inside most default chunk sizes, those
  decisions mostly cancel out.
- **Not FiQA quality, for any entrant.** The cost tables now include FiQA for the two .NET
  entrants, but no entrant has been *scored* on it — the empty entries in `BeirReproduction` are
  still waiting, at a derived ~1 h each. A corpus appearing in the cost section is not a corpus the
  quality table covers.
- **A dated measurement of pinned versions.** Every library here ships faster than this table
  re-measures. The dates and versions are on every figure so that staleness is visible rather than
  denied.
