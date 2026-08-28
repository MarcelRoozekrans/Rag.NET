# Pipeline parity — hold a real `AddRagNet` pipeline to the harness's top-k

**Phase:** 6.2.1 — Retrieval & Answer Sweep. One thread of the sweep, not the phase.
**Status:** design, 2026-08-27.
**Surface:** Backend.

## The gap

Every pinned figure this project publishes is produced by `BeirHarness`. It embeds with
`OnnxEmbeddingGenerator`, stores and scores through `InMemoryVectorStore`, aggregates with
`DocumentRanking` and scores with `IrMetrics` — all the library's own parts, which is the point of
its design. What it does **not** do is go through `AddRagNet`.

A user does. And the two paths are not the same path:

| | Harness | Shipped pipeline |
| --- | --- | --- |
| Query vector | `EmbeddingCache` over the generator, salted | `QueryVectorResolver.ResolveAsync` |
| Search options | `TopK` only | `TopK` + `MinScore` + `MetadataFilter` |
| Behaviours before the store | none — calls `store.SearchAsync` | **sixteen** |
| Result shape | chunk hits, then `DocumentRanking` pools to documents | chunk-level `SearchResult` |

`RetrievalPipelineBuilder` always registers the full chain — `SelfQuery`, `ResultCache`,
`LostInTheMiddle`, `ContextBudget`, `Mmr`, `RedundancyFilter`, `ParentDocument`, `Reranking`,
`RetrievalGuard`, `Adaptive`, `CorrectiveRag`, `MultiQuery`, `Hyde`, `EmbeddingCache`, `Filter`,
`Ensemble`, `VectorStore` — and every one of the first sixteen is supposed to no-op at shipped
defaults.

**Nothing asserts that.** A behaviour that quietly stops no-opping changes what every user gets
while every pinned figure goes on describing the old path, and the suite stays green. That is the
gap Phase 5.2.2 named and this thread closes.

The risk is not hypothetical in kind. This project has twice shipped defects that no test could
reach — #332 and #333 in a published package — because the fixtures could not produce the inputs
that fail.

## What is being claimed

> Given the **same embedder instance**, the **same pre-populated `IVectorStore` instance**, the same
> query and the same `TopK`, a real `AddRagNet` pipeline returns byte-identical results to the
> harness's direct `store.SearchAsync`.

Instances are shared by identity, not reconstructed:

```csharp
services.AddSingleton<IVectorStore>(sharedStore);          // the instance the harness indexed
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sharedGenerator);
services.AddRagNet();                                      // no configuration — shipped defaults
```

Holding the store, the vectors, the scoring and the corpus fixed *by identity* is what leaves
exactly one variable: the sixteen behaviours. Rebuilding an equivalent store instead would
reintroduce indexing as a second variable and make a failure unattributable.

**This holds by identity in both legs.** `BeirHarness.RetrieveScoredRunsAsync` builds its own store
and its `RetrieveAsync` is private, so the harness's *measurement entry points* cannot share a store
— but `AblationRow.Dense` is public and takes the store as a parameter, which is the road both legs
take. No visibility change to `BeirHarness` is required.

### Level and tolerance

**Chunk level, ordered, scores compared exactly — in both legs.**

- **Chunk level, not document level.** `DocumentRanking.TopDocuments` max-pools chunks into
  documents — a harness concern; the product does not do it. Pooling is lossy, so asserting after
  it lets a chunk-order difference vanish into a document-level tie. The chunk list is the strict
  claim and the document projection follows from it.
- **Exact scores, no tolerance.** Both sides call the same `SearchAsync` on the same store, so
  identical inputs give bit-identical floats. There is no legitimate source of small differences,
  so a tolerance could only hide illegitimate ones — in particular a query vector that differs
  because `QueryVectorResolver` and the harness's `EmbeddingCache` disagree, which is among the
  likeliest real divergences.
- **`TopK` is set explicitly on both sides.** `RetrievalOptions.TopK` defaults to 5 while the
  harness computes its own cutoff; letting each use its default would fail for a reason that is not
  drift.

## Approach

Three were considered.

**A — comparison test (chosen).** Build both sides, run the same queries, assert identical output.
The harness is not modified.

**B — a shared seam both sides call.** Eliminates divergence in the innermost step, and only there:
the sixteen outer behaviours — the actual risk — stay unobserved. It buys the weaker half.

**C — route the harness's dense row through `AddRagNet`.** Complete elimination, and the most
dangerous option. If any of the sixteen is not a true no-op, **every pinned figure moves**, and a
fix cannot be distinguished from a regression without re-measuring the whole programme.

**A is chosen for its sequencing.** The harness is the measuring instrument for a sweep that is one
technique into ten. B and C both modify the instrument mid-measurement; A observes the same fact
without touching it. If A finds drift, that finding is what earns B or C later, with the divergence
already characterised rather than guessed at.

## The two legs

### Fast leg — every push, no provisioning

A small synthetic corpus and a deterministic in-test embedder, indexed into a real
`InMemoryVectorStore`. Runs unconditionally. This is the leg that catches a lost no-op at the commit
that causes it.

**Its fixture is the part that needs care, because this exact failure has already cost this project
two shipped defects.** 6.2.3's mock embedder constructed `new Random(123)` *inside* its callback, so
every vector came back byte-identical. Identical points collapse to one cluster, no test ever built
a RAPTOR tree deeper than one level, and #332, #333 and an unbounded-spend infinite loop all stayed
unreachable while the suite was green.

A degenerate embedder would fail this test the same way and worse: ties make reordering invisible,
so the assertion would pass by construction. The fixture therefore carries a contract, asserted by
**its own separate test** rather than trusted:

- **deterministic** — same text, same vector, every call (both sides must receive the identical
  vector);
- **injective over the fixture corpus** — distinct texts give distinct vectors;
- **strictly ordering** — scores over the corpus are pairwise distinct, so the top-k has exactly one
  correct order and any reordering or truncation is observable.

The corpus is sized so `TopK` is strictly less than the document count; otherwise truncation cannot
show.

### Real leg — opt-in, gated on provisioning

Loads SciFact through the existing `BeirHarness.LoadAsync` / `IsProvisioned` path and runs a fixed
sample of queries through both sides. This is the leg entitled to the phrase *"the harness's top-k"*,
because it is the harness, on the corpus the figures come from.

**It shares one store by identity and compares at chunk level, exactly as the fast leg does.** An
earlier draft of this design had it comparing document-level rankings over two separately-indexed
stores, on the reasoning that `RetrieveScoredRunsAsync` builds its own store and `RetrieveAsync` is
private. That was solving the wrong problem — there is a public road:

- **`AblationRow.Dense` is public and takes the store as a parameter** ("The populated store. The
  row never writes to it."), returning `IReadOnlyList<ChunkHit>` directly. It is the harness's own
  dense row — documented as *"why this row's parity numbers are the ones validated against published
  figures, and why they must not move"* — so the comparison is against harness code, not a replica
  of it.
- **`BeirHarness.EmbedAsync` is `internal`** and `PipelineParity` lives in the same assembly, so the
  test can index one store itself and hand the same instance to both sides.
- **`CachingEmbeddingGenerator` already exists** — `OnnxEmbeddingGenerator` behind `EmbeddingCache`,
  in the `IEmbeddingGenerator` shape the pipeline takes. Registering it means both sides read the
  *identical cached vector*, rather than the pipeline calling a live generator that could disagree
  with a cache populated under a different model revision.

So the store is indexed once and shared, there is no two-store equivalence question, and no
visibility change to `BeirHarness` is needed. The only code this leg writes that the harness also
has is indexing — and because both sides read the one store it produced, it cannot be a source of
difference.

It **skips, never fails**, when the model or the dataset cache is absent — the convention every
other case in this project follows.

Gated on **provisioning only, not `RAGNET_BEIR_LONG_RUNS`.** The embeddings are cached and a fixed
sample is seconds; the long-run gate exists for hour-scale sweeps. Putting the honest leg behind it
would mean it effectively never runs.

## Failure semantics

**The test asserts parity, not correctness.** When the two sides disagree, nothing in the test knows
which is right — that is a judgement about intent. The message reports evidence and names both
readings:

> Rank 3 differs — pipeline `doc-17#2` (0.7413…) vs harness `doc-04#1` (0.7413…). Either a default
> retrieval behaviour stopped being a no-op, or the harness's dense path changed. If the behaviour
> change was deliberate, every pinned figure now describes something the shipped pipeline no longer
> does.

It reports **the first differing rank with both chunk ids and both scores**. A diff at rank 1 and a diff at rank 9 of 10 are different bugs, and equal scores with
different ids is a tie-break divergence rather than a vector divergence — a different cause
entirely.

**A deliberate change should fail this test.** That is the feature. Someone flipping a default so
`MmrBehavior` or `RetrievalGuardBehavior` does something at defaults should be stopped and made to
notice that the pinned figures have stopped describing the product. The remedy is to update the test
consciously, which is the moment to decide whether re-measurement is owed.

Three mechanical requirements:

1. `IRagPipeline.RetrieveAsync` returns `Result<IReadOnlyList<SearchResult>, RagError>`. A
   `RagError` is a **failure to run**, not a parity mismatch: it gets its own message quoting the
   error, never a comparison against an empty list.
2. **Each query runs once per side, against a freshly built container.** `ResultCacheBehavior` and
   `EmbeddingCacheBehavior` are both in the default chain; if either is not a no-op, a re-run could
   agree where a first run did not. Fresh containers keep the test on the first-call path, which is
   what a user gets.
3. **Several queries, not one.** About ten in the fast leg, a fixed sample in the real leg. A
   divergence affecting only queries with certain properties — short, empty result set, scores under
   a threshold — is exactly what `RetrievalGuardBehavior` or `MinScore` handling would produce.

## Components

All in `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`, which already references `Rag.NET` and
`Rag.NET.Benchmarks.Quality`, declares neither `RequiresDocker` nor `RequiresLlm`, and therefore
already runs in the fast tier on every push with expensive cases self-skipping. **No new test
project is needed.**

| File | Purpose |
| --- | --- |
| `PipelineParity.cs` | Builds a default `AddRagNet` container over a supplied store and embedder, runs a query through `IRagPipeline.RetrieveAsync`, projects the results to `ChunkHit` using the same `"{DocumentId}#{ChunkIndex}"` shape `AblationRow.ToChunkHits` uses, and compares against an expected `IReadOnlyList<ChunkHit>` — ids and exact scores, in order. **One comparison mode, shared by both legs.** Produces the diagnostic message above. |
| `OrderingEmbeddingGenerator.cs` | The fast leg's fixture embedder — deterministic, injective, strictly ordering. |
| `OrderingEmbeddingGeneratorTests.cs` | The guard on that contract, separate so it fails on its own terms. |
| `PipelineParityTests.cs` | The two legs. |

### A naming collision, flagged deliberately

In this codebase **"parity" already means something else** — `BeirParityTests`, "the parity leg",
"every vector store reproduces the SciFact parity figure" all refer to reproducing published BEIR
numbers. The ROADMAP and the Definition of Done both call this thread "pipeline-parity", so the name
is kept for traceability, but `PipelineParity`'s doc comment must open by disambiguating it. Without
that, a future reader will reasonably read it as another BEIR protocol leg.

## The risk this design is most exposed to

**This test will almost certainly pass the moment it is written.** That is the expected state: if
the sixteen behaviours already no-op, there is no drift to find.

A test that has never failed is not evidence. It may be asserting a tautology — which is exactly how
this project's last three fixture defects survived. **The work is not done when it goes green; it is
done when it has been made to go red on purpose.**

The implementation plan must therefore carry an explicit **mutation check**: enable one default
behaviour (`UseMmr` is the natural candidate), confirm the test fails *and* that the message
correctly names the first differing rank, then revert. Per the convention 6.2.12 established, the
mutation is verified to compile before it is run. Without this step the deliverable is a green light
wired to nothing, and there is no point building it.

## Out of scope

- **Any change to `BeirHarness`.** Approach A's premise is that the instrument stays fixed.
- **Fixing any drift that is found.** A divergence is a finding to report and file. Repairing it
  would move pinned figures, which is a decision with its own evidence requirements and its own
  thread.
- **Arms beyond the dense default.** Hybrid BM25, reranking, HyDE, graph and RAPTOR arms do not all
  map one-to-one onto registered behaviours, and each correspondence is a judgement call rather than
  one rule. Dense is the path every control arm uses and the one a user gets from `AddRagNet` with
  no options. Further arms become their own threads, better informed if this one finds anything.

## Definition of done

- The fast leg runs on every push with no provisioning and asserts chunk-level, ordered,
  exact-score parity over about ten queries.
- The fixture embedder's determinism, injectivity and strict ordering are asserted by a separate
  test that fails on its own terms.
- The real leg makes the same chunk-level claim over SciFact with the real ONNX embedder, against
  `AblationRow.Dense` over one shared store, and skips rather than fails when unprovisioned.
- A failure names the first differing rank with both ids and both scores.
- **The mutation check has been run**: one default behaviour enabled, the test observed failing with
  a correct message, the mutation reverted.
- 6.2.1's exit-condition clause *"the pipeline-parity test is in the fast tier"* is satisfied, and
  the ROADMAP records the thread — **without marking the phase complete**.
