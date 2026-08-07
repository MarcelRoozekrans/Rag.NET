# Converting Hand-Written Spans to ZeroAlloc.Telemetry — Assessment

**Date:** 2026-08-07
**Milestone:** 4 — Release Readiness
**Status:** superseded by §8 — v1.6.0 shipped the blocking fix; a reranker pilot is now in review
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

---

## 8. Superseded: v1.6.0 landed the fix, and a pilot was run

**[#53](https://github.com/ZeroAlloc-Net/ZeroAlloc.Telemetry/issues/53) shipped in v1.6.0** the
same day it was filed. `[Trace("ragnet.rerank.{type}")]` substitutes the wrapped implementation's
type name, and — checked in the generated source, not assumed — **composes it once in the proxy
constructor**, so it costs nothing per call. §4's blocker is gone.

A pilot converted `IReranker` (Cohere + Onnx) to measure the rest. Four claims were open; **three
resolved against what this document originally said.**

### 8.1 Blocker one was overstated here

§3 called `Rag.NET.Abstractions` an assembly Phase 4.7 kept free of dependencies. **It is not, and
was not when that was written.** Its packed nuspec already lists six, four of them ZeroAlloc:
`Results`, `Specification`, `Validation`, `ValueObjects`. A seventh with no transitive
dependencies is a far smaller step than §3 implies.

### 8.2 `PrivateAssets="all"` looked like the mitigation and is a trap

The obvious move — reference the package privately so it stays out of the nuspec — **works for
packaging and breaks the assembly.** The nuspec came out clean, but the attributes still land in
metadata while the assembly is absent at runtime, so `typeof(IReranker).GetCustomAttributes()`
throws `FileNotFoundException`. That is worse than a declared dependency: an unresolvable-assembly
error from a package that declares no such dependency.

**No `PrivateAssets` is needed at all.** NuGet's default already makes analyzers, build and
contentFiles private — which is why every other reference here packs as `exclude="Build,Analyzers"`
— so a plain `PackageReference` flows the attributes and keeps the generator out of consumers'
builds. Guarded by `InstrumentAttributeReflectionProbe`, in a test project that does not reference
ZeroAlloc.Telemetry and therefore stands in for a consumer.

### 8.3 The allocation question, finally measured on the right shape

§5 recorded a contradiction and declined to resolve it. Measured here on an `async Task`
interface method, under a listener, tags set:

| | Mean | Allocated |
|---|---:|---:|
| No instrumentation | 19 ns | **72 B** |
| Span inside the method (Phase 4.4) | 565 ns | **632 B** |
| Generated proxy (pilot) | 627 ns | **728 B** |

**The proxy costs 96 B more per call, ~15%** — the second async state machine, as predicted.
Neither §5 figure described this shape: both were measured without a listener, where
`StartActivity` returns null and everything is free.

Trust the allocation column, not the timings: this was `--job short` and the baseline's error
exceeds its mean. For a reranker, called once per query, 96 B is immaterial. **Per-chunk
instrumentation would be a different conversation** and must be measured separately rather than
assumed to inherit this verdict.

### 8.4 The finding nobody predicted: instrumentation became a composition concern

With hand-written spans, telemetry was a property of the type — construct a `CohereReranker` and
it traced. With a proxy, telemetry is applied by whoever wires the object up. **A directly
constructed reranker now emits nothing**, which is why both telemetry tests had to be rewritten to
wrap their subject.

`UseReranking<T>()` applies the proxy centrally, so every reranker package — and any added later —
gets it without opting in. But this is a real behavioural change for anyone constructing
components by hand, and it deserves an explicit decision before it reaches thirteen packages.

### 8.5 What is unverified

`OnnxRerankerTelemetryTests` is env-gated on `RAGNET_ONNX_RERANK_MODEL` and **skipped** in the
pilot run. Its span-name update is written but has never executed. Only the Cohere path has been
observed working end to end.

### 8.6 The decision this leaves open

Rolling out repo-wide means accepting §8.4, and reversing the backend-as-tag convention Phase 4.4
chose deliberately — `docs/reference/opentelemetry.md` now documents reranking as an explicit
exception to its own rule, which is tolerable for a pilot and not tolerable indefinitely. The
vector stores are the real test: six backends, 17 spans, and 17 tags reading instance
configuration that attributes still cannot reach.
