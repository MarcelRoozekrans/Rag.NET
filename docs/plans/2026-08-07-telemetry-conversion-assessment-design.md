# Converting Hand-Written Spans to ZeroAlloc.Telemetry — Assessment

**Date:** 2026-08-07
**Milestone:** 4 — Release Readiness
**Status:** decided — **do not convert.** v1.6.0 removed the original blocker; a pilot was built,
measured and reverted. See §8.
**Origin:** *"Make a phase to write out all the hand written spans into the generated ones. I want to
use as much as possible from the zeroalloc ecosystem."*

## 0. Outcome

**Nothing is converted.** §§1–7 record why that was true for v1.5.0; §8 records v1.6.0 removing
the blocker, a pilot being built and measured, and the decision standing anyway for different
reasons. The earlier sections are kept as written rather than rewritten — several of their claims
were corrected by the pilot, and the corrections are more useful next to what they replaced.

**No site met the bar as of v1.5.0, so nothing was converted.** This is a measurement, not a preference: two
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

> **Resolved in §8.3.** The guess above was right and the framing was wrong: the two figures were
> never in conflict. Both are reproduced in one table, and the no-listener case turned out to
> decide the whole question.

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
  would be answering a question nobody is asking. *(Wrong. §8.3 measured it once the blocker
  lifted, and the idle-path result became the deciding evidence. Left standing as written, because
  "nobody is asking" is a poor reason not to measure something.)*

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

### 8.3 The allocation question, and why §5's "contradiction" was nothing of the kind

§5 recorded two disagreeing figures and declined to resolve them. Measured here on an
`async Task` interface method, with and without a listener:

| | Listener | Allocated |
|---|---|---:|
| No instrumentation | ✗ | 72 B |
| **Span inside the method** | ✗ | **72 B** |
| **Proxy around the method** | ✗ | **144 B** |
| No instrumentation | ✓ | 72 B |
| Span inside the method | ✓ | 632 B |
| Proxy around the method | ✓ | 728 B |

**Both earlier figures are reproduced in this one table, and both were correct.** Phase 4.4's
144 B is the no-listener proxy shape; ZeroAlloc.Telemetry's published 72 B parity is the
no-listener in-method shape. They were answering different questions, which is why they appeared
to contradict. Nothing was wrong except the assumption that they were comparable.

The decisive row is the third. **With no listener attached, the proxy allocates double — 144 B
against 72 B — while the in-method span allocates nothing at all.** `StartActivity` returning
null removes the `Activity`; nothing removes the wrapper's async state machine, which is
allocated unconditionally.

That is the common case. Most consumers run with no listener most of the time, and this
repository documents "zero-overhead when no listener is attached" as a property. The in-method
approach holds that property exactly, byte for byte. **A proxy cannot.**

Under a listener the gap is proportionally smaller — 96 B, ~15% — and for a reranker called once
per query it would be immaterial. It is the *idle* case that decides this, not the observed one.

Trust the allocation column rather than the timings: the run was `--job short` and the baseline's
error exceeds its mean. Allocations are exact byte counts and stable across runs.

### 8.4 The finding nobody predicted: instrumentation becomes a composition concern

With hand-written spans, telemetry was a property of the type — construct a `CohereReranker` and
it traced. With a proxy, telemetry is applied by whoever wires the object up. **A directly
constructed reranker now emits nothing**, which is why both telemetry tests had to be rewritten to
wrap their subject.

`UseReranking<T>()` applies the proxy centrally, so every reranker package — and any added later —
gets it without opting in. But this is a real behavioural change for anyone constructing
components by hand, and it deserves an explicit decision before it reaches thirteen packages.

### 8.5 What was never verified

`OnnxRerankerTelemetryTests` is env-gated on `RAGNET_ONNX_RERANK_MODEL` and **skipped** throughout.
Only the Cohere path was ever observed working end to end. Since the pilot was reverted this no
longer matters, but it is recorded because it would have shipped unverified had the decision gone
the other way.

### 8.6 The decision: reverted

**The pilot was built, measured, and then reverted.** The code is not on `main`; this section and
the benchmark are what it produced.

The number that decided it is §8.3's third row — **double allocation with no listener attached**,
which is how most consumers run most of the time, and which contradicts a zero-overhead property
this repository documents and currently holds exactly.

The line count decided the rest. Converting two packages was **+110/−19 to delete seven lines of
telemetry boilerplate**, and 47 of the added lines were a reflection probe guarding a hazard that
only existed because of the change. The ratio does not improve at scale: the vector stores add 17
tags reading instance configuration that attributes still cannot reach, so those methods would
carry attributes *and* `Activity.Current` — two mechanisms where there is now one.

Set against that, the gains were a uniform error status (enforceable with a convention test if it
matters) and the removal of one-to-four-line spans. The stated goal was **less boilerplate**; this
route measurably adds it.

Not adopted, and worth separating from the above because it is not a criticism of the library:
ZeroAlloc.Telemetry did everything asked of it. Four issues were filed and all four shipped the
same day. The package is better for it, and the mismatch is with this repository's shape —
instrumentation living inside already-`async` methods on interfaces with many implementations —
not with the library's quality.

**What survives:** these measurements, the benchmark covering a shape the suite never covered, and
the four upstream issues. Revisit only if the interfaces stop being many-implementation, or if a
future version can instrument without an extra async frame.
