# Library Comparison at Defaults — Design (Phase 3.14)

**Date:** 2026-08-02
**Milestone:** 3 — Quality Hardening & Evaluation, Phase 3.14 — the last
**Framed by:** the Phase 3.12 design, which rejected matched configuration as the wrong target

## 0. What is already decided, and is not reopened

- **Each library at its own defaults**, not matched configuration. A matched table measures how
  carefully each library was configured; match the model, chunk size and top-k across four
  libraries and they converge, because at that point they are the same embedding model behind
  different syntax.
- **Every configuration published in full**, with exact versions.
- **A Rag.NET default that loses is a finding, not a bug to tune away.** Changing any default in
  response to this table is explicitly out of scope and is its own phase.
- **Both ecosystems**: .NET comparators in-process, Python comparators in a subprocess.
- **Published**, with the harness committed and re-runnable.

## 1. They retrieve. We score. One metric implementation.

`IrMetrics.Evaluate` takes `IReadOnlyDictionary<string, IReadOnlyList<string>>` — query id to a
ranked list of document ids — and nothing else. It needs no scores, no embeddings, and no library
internals.

**So no entrant computes a metric.** Every library retrieves and emits a ranked list; all lists are
scored by the same `IrMetrics` that produced this repository's published BEIR figures.

This removes the largest confound in any cross-library comparison. If each library scored itself,
every difference would have two possible causes — its retrieval or its evaluation — and nDCG
implementations genuinely differ (tie-breaking, the IDCG cap, whether unjudged documents are
dropped). Phase 3.12 already had to pin `ignore_identical_ids` and the `min(|relevant|, k)` cap to
match published figures; those choices must be made **once**, for everyone.

It also collapses the Python half from "reimplement the BEIR protocol in Python" to "write a JSON
file". **That is the difference between a phase that is mostly infrastructure and one that is
mostly measurement.**

The interchange is a run file — query id, document id, rank — in the TREC format the IR community
already uses, so an outside reader can score our run files with `trec_eval` and check us.

## 2. The pinned embedder is a matched element, and saying so is the design's honesty

The roadmap says "same corpus and the same embedding model". **That is a departure from pure
defaults and must be labelled as one**, because every library also ships a *default embedder*, and
that is arguably the most consequential decision it makes on a user's behalf.

Two tables were possible and only one is worth building:

- **Each library's own default embedder** — measures embedding-model choice, which then dominates
  everything else. A library defaulting to a large hosted model wins on quality and loses on cost,
  and the table becomes a proxy for "who defaults to the most expensive API".
- **One pinned embedder for all** — measures the chunking, indexing and retrieval decisions each
  library makes once the model is held constant. That is the comparison a reader choosing a library
  can act on.

**We pin the model** — `all-MiniLM-L6-v2` at the revision Phase 3.7 already pinned and SHA-256
verified — and **the table states in its own header that the embedder is matched and everything
else is default.** A reader must not think this is defaults end-to-end.

Each library's own default embedder is **published in the configuration table** even though it is
not used, because "this library would otherwise have used X" is information a reader needs to
interpret the row.

## 3. The in-process/subprocess split is a latency confound only

.NET comparators run in the existing test host; Python comparators run as a subprocess. That is a
real methodological difference, and it is worth being precise about what it does and does not touch.

**It cannot affect quality.** nDCG@10 over a ranked list is unaffected by how the process producing
that list was launched. The run file is the boundary, and it carries no timing.

**It would wreck latency**, so **latency is not published across the boundary.** Timings appear only
within an ecosystem, if at all, and are labelled as such. A table comparing a warm in-process .NET
call against a Python subprocess start would be measuring the harness.

This is the honest resolution: the confound is named, bounded to one dimension, and that dimension
is withheld rather than reported with a caveat nobody reads.

## 4. What "defaults" means, precisely, and per library

"Default" is not self-evident, and an unstated interpretation is how these tables get argued with.
For each entrant the phase records, from the library's own source or documentation:

- default chunker and its size and overlap
- default top-k
- default retrieval mode (dense, hybrid, anything else)
- whether it reranks by default
- its default embedder (§2, published though unused)

**Where a library has no default** — it requires a choice before it will run — that is itself a
finding about the library and is recorded as "no default; chose X because the corpus requires
something", rather than quietly picking a value that flatters or punishes.

Every value is cited to a file and version, so a reader can check the reading rather than trust it.

## 5. Staged, because the .NET half is publishable alone

**Stage 1 — .NET**: Rag.NET, Semantic Kernel, Kernel Memory, in-process, on the existing harness
with `EmbeddingCache` and the BEIR descriptors. This is measurement, not infrastructure.

**Stage 2 — Python**: LangChain, LlamaIndex, Haystack, in a pinned subprocess emitting run files.

Staging is not scheduling convenience. **Stage 1 proves the run-file boundary and the scoring path
on libraries we can debug in-process.** If the interchange is wrong, it is far cheaper to discover
that in .NET than across a language boundary, and Stage 1 alone is a publishable table for the
audience most likely to be choosing.

**If Stage 2 proves unaffordable, Stage 1 ships and Stage 2 is recorded as unrun** — with what it
would have cost — rather than the phase stalling or the Python numbers being estimated.

## 6. Reproducibility is the only thing that makes this defensible

Publishing numbers about other people's software is a claim, and a claim nobody can check is worth
less than no claim.

- **Every entrant's exact version pinned**, in a lockfile for Python and a package reference for
  .NET.
- **The harness committed** and runnable by an outsider.
- **Run files published** alongside the table, so a reader can re-score with `trec_eval` and get
  our number.
- **The corpus and protocol are the ones already published** in `docs/reference/retrieval-quality.md`,
  so our own row is checkable against figures that already exist.

**Rag.NET's own row is a control, not the headline.** It runs through the same run-file boundary as
everyone else, and its number must reproduce the figure the BEIR harness already publishes. **If it
does not, the harness is wrong and no other row can be trusted** — that check comes before any
comparison is read.

## 7. What this does not measure

Stated so the table cannot be read as more than it is.

- **Not ingestion throughput, memory, or cost.** One quality axis, on one corpus family.
- **Not production suitability.** A library can retrieve well and be unpleasant to operate.
- **Not the libraries' ceilings.** Every entrant, ours included, would score differently tuned. That
  is the point of a defaults table and also its limit.
- **Not a moving target.** These libraries change fast; the table is a dated measurement of pinned
  versions, and it says so beside the numbers rather than in a footnote.
- **Not statistically significant on one corpus.** Where the phase reports differences smaller than
  the spread already observed between protocols in Phase 3.15, it says they are not separable.

## Out of scope

- **Changing any Rag.NET default in response.** Measure first; a defaults change is its own phase.
- **Adding datasets.** SciFact, FiQA and ArguAna are what the harness has pinned and reproduces.
- **Latency across the ecosystem boundary** — §3.
- **Tuning any entrant, including ours.** A table of defaults where one row was tuned is not a table
  of defaults.
