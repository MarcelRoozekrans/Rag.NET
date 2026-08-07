# Converting Hand-Written Spans to ZeroAlloc.Telemetry — Assessment

**Date:** 2026-08-07
**Milestone:** 4 — Release Readiness
**Status:** decided — **do not convert**; file the blocking gap upstream
**Origin:** *"Make a phase to write out all the hand written spans into the generated ones. I want to
use as much as possible from the zeroalloc ecosystem."*

## 0. Outcome

**No site meets the bar, so nothing is converted.** This is a measurement, not a preference: two
independent blockers each disqualify every traced type in the repository, and neither is addressed
by the release that unblocked the tag work.

The phase's deliverable is therefore the measurement, one upstream issue for the gap that would
change the answer, and this record — so the next person to have this idea starts from evidence
rather than repeating the evaluation.

## 1. What ZeroAlloc.Telemetry v1.5.0 fixed

Phase 4.4 evaluated v1.4.1 and rejected it, principally because **it could not set span tags at
all**: of the traced units examined, zero had all their wanted tags expressible. Three issues were
filed. All three shipped in **v1.5.0** (2026-08-07):

| Issue | Shipped as |
|---|---|
| [#35](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/35) — `[TraceTag]` cannot reach a member of a parameter | `[TraceTag]` reads a member path of an argument |
| [#36](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/36) — no way to set a constant tag | `[TraceTagConstant]` |
| [#37](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/37) — `[TraceTagFromResult]` is unconditional | `When` on `[TraceTagFromResult]` |

**These genuinely worked.** Tag expressiveness went from roughly 7% to roughly 50%. The upstream
work was not wasted; it simply was not the whole problem.

## 2. The measurement, and a correction to it

**48 hand-written spans, 131 tags, across 30 files.**

The first count taken was 68 spans and 175 tags. That was wrong: it included `obj/`, where the
ZeroAlloc.Rest and ZeroAlloc.Mediator generators emit their own instrumentation. Twenty spans and
forty-four tags in that figure were generated code this phase could never convert. The corrected
numbers are used throughout. *An unfiltered grep is not a measurement either.*

All 131 tags classified by the shape of their value expression:

| Category | Count | Expressible in v1.5.0? |
|---|---|---|
| Literals (`"drop"`, `0`, `true`, `nameof(…)`) | 26 | yes — `[TraceTagConstant]` |
| Parameter member paths (`ctx.Metadata.DocumentId.Value`) | ~27 | yes — `[TraceTag]` |
| Result-derived (`results.Count`) | ~12 | yes — `[TraceTagFromResult]` |
| `GetType().Name` | 21 | only by hardcoding the type name per class |
| Instance config (`_indexName`, `_options.CollectionName`) | 17 | no — reads `this`, not arguments |
| Computed locals (`matchCount`, `inputTokens`) | ~28 | no — mid-method values |

So roughly a third of tags would still be set by hand. A converted site would carry an annotated
interface, a generated proxy that opens the span, **and** hand-written `Activity.Current?.SetTag`
calls inside the method body for everything the attributes cannot reach — more moving parts than
today's `using var activity = …; activity?.SetTag(…)`, not fewer.

That alone would be a reason for caution. It is not the reason for the decision.

## 3. Blocker one: every traced interface lives in `Rag.NET.Abstractions`

`[Instrument]` goes on the **interface** — confirmed against the v1.5.0 README, not carried over
from the 1.4.1 evaluation. Every interface implemented by a traced type is declared in
`Rag.NET.Abstractions`: `IVectorStore`, `IReranker`, `IRetrievalGuard`, `IChunkSanitiser`,
`IAnswerEngine`, `IRetriever`, `IIngestor`.

Annotating any of them puts a package reference in the most foundational assembly in the
repository — **inverting what Phase 4.7 achieved**. The dependency is small (attributes plus a
generator, no transitive NuGet dependencies), so this blocker alone might be arguable. It does not
stand alone.

## 4. Blocker two: one span name for every implementation

This is the decisive one, and it is the mirror image of what v1.5.0 fixed.

`[Trace("name")]` is written on the interface method, so **every implementation of that interface
produces the same span name**:

| Interface | Implementations (approximate) |
|---|---|
| `IRetrievalBehavior` | ~23 |
| `IIngestionBehavior` | ~16 |
| `IVectorStore` | ~10 |
| `IAnswerEngine` | ~9 |
| `IReranker`, `IRetrievalGuard`, `IChunkSanitiser` | 4–5 each |

Not one traced type has an interface to itself.

**Phase 4.4 exists precisely to defeat this.** Its motivating complaint was that a user seeing slow
retrieval got one generic span and a `vector_store` tag holding a type name, and could not tell
whether the store, the reranker or graph traversal was the cost. Converting would reintroduce that
for every traced type — Qdrant indistinguishable from Weaviate, all 23 retrieval behaviours sharing
one name. The 21 `GetType().Name` tags in §2 exist for exactly this reason: they are the hand-written
workaround for a distinction the attribute model cannot express.

**A conversion that made traces less informative than they are today would be a regression wearing
the costume of a cleanup.**

## 5. An open discrepancy, deliberately not resolved

Phase 4.4 measured, in this repository: bare call **72 B**, hand-written no-op decorator **144 B**,
generated proxy **144 B**. The v1.5.0 README publishes: direct call **72 B**, hand-written
instrumentation **72 B**, generated proxy **72 B** — parity.

These disagree, and **neither is quoted here as fact**. The likely explanation is that the shapes
differ: this repository's traced methods are `Task`-returning `async` interface methods, where
wrapping one async method in another allocates a second state machine, while the upstream benchmark
may measure a shape where that does not arise.

It is left unresolved because it cannot change the decision — §3 and §4 are architectural, not
performance-driven. If the blockers are ever lifted, **re-measure in this repository on these
shapes** before trusting either number.

## 6. What would change the answer

One upstream feature, and it is worth more than the three already delivered: **a span name derived
from the implementing type.**

With it, `[Instrument]` on `IIngestionBehavior` and `IRetrievalBehavior` would give each of the ~39
implementations its own span name automatically. That converts blocker two from a disqualification
into the largest opportunity in the repository, and it deletes the 21 `GetType().Name` tags as a
side effect rather than requiring them to be hardcoded per class.

Secondary, worth filing only if the first lands: **reading instance state**, which would cover the
17 `_indexName` / `_options.CollectionName` tags.

Blocker one (§3) would remain a judgement call even then — whether a zero-transitive-dependency
attributes package in `Abstractions` is an acceptable price. That is a decision for the day it
becomes relevant, not now.

## 7. Out of scope

- **Converting anything.** The subject of the decision.
- **Removing the `GetType().Name` tags.** They are the workaround for §4 and stay until the
  workaround is unnecessary.
- **Re-running the allocation benchmark.** §5 — it cannot change the outcome, and measuring it now
  would be answering a question nobody is asking.
