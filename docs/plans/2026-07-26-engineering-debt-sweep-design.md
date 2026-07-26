# Engineering Debt Sweep — Design (Phase 2.1)

**Date:** 2026-07-26
**Milestone:** 2 — Deferred Items & Technical Debt, Phase 2.1
**Covers:** the six review-cycle debts recorded in `docs/planning/ROADMAP.md`

Six independent fixes. They share no code and can land in any order; they are batched into
one phase because each is too small to justify its own.

Two of the six turned out to be worse than the recorded note said. Both are called out below
(items 1 and 3) because they change what "done" means.

## Scope decisions (agreed)

1. The shared filename sanitizer is adopted by **all nine** connectors that synthesize a
   filename from user-controlled text, not only the three that already have a helper.
2. Transport failures get a **new `RagError.TransportFailed(Exception)`** union case.
3. `ConfigureResilience` is **wired via decorators**, not removed.
4. Score-scale mismatch is solved with a **capability interface**, matching the repo's
   existing `IHybridSearchable`/`ISparseSearchable` convention.

---

## 1. Shared filename sanitizer

**Problem, as recorded:** three verbatim copies of `SanitizeFileName`.

**Problem, as found:** two copies (Exchange `ExchangeMailDataProvider.cs:299`, Linear
`LinearDataProvider.cs:331`) plus a third *inlined* in Gmail (`GmailDataProvider.cs:92-110`)
that cannot be reused by anyone. More importantly, **six further connectors sanitize nothing
at all** — Confluence, Asana, Notion, Airtable, Slack, Microsoft Teams — and Teams channel
names routinely contain `/` and `:`.

And the algorithm all three share is host-dependent: `Path.GetInvalidFileNameChars()` returns
41 characters on Windows but only `'\0'` and `'/'` on Linux/macOS. The same message ingested
on a Linux host keeps `:` `*` `?` `"` `<` `>` `|` and control characters in its filename.

**Design:**

- New `public static class FileNameSanitizer` in `src/Rag.NET.DataProviders/`. Public is
  forced, not preferred: each connector is a separate assembly and NuGet package, and the
  sibling `FileExtensionMatcher` is `internal` with `InternalsVisibleTo` for the test project
  only — making this internal would need 20 more entries.
- Pins a **deterministic** invalid set (the Windows superset: `< > : " / \ | ? *`, `0x00-0x1F`)
  via a cached `SearchValues<char>`, so a filename does not depend on the host OS.
  `Path.GetInvalidFileNameChars()` allocates a fresh array per call and is not used.
- Replacement character `'_'`, matching all three existing implementations.
- Handles what none of them handle: trims leading/trailing whitespace and trailing dots
  (invalid on Windows), collapses an all-invalid or empty result to a caller-supplied
  fallback, and caps length (the extension is the caller's business, so the cap applies to
  the sanitized stem).
- Reserved Windows device names (`CON`, `PRN`, `NUL`, `COM1`…) are **out of scope**: these
  names never touch a filesystem — `FileHandle.FileName` is metadata used for parser
  selection and display. Documented on the helper so the omission is deliberate rather than
  forgotten.
- Adopted by all nine synthesizing connectors. The three existing sites converge on one
  behavior: the empty-subject fallback is sanitized uniformly (today Gmail sanitizes it,
  Exchange bypasses it, Linear has none).

**Not in scope:** `WebCrawlerDataProvider.InferFileName` and its RSS/Sitemap callers derive
names from URL path segments — a different input domain with its own decoding concerns.

**Testing:** unit tests for the helper (each invalid class, trimming, trailing dots, empty →
fallback, length cap, and a determinism test asserting the set does not vary with
`Path.GetInvalidFileNameChars()`). Existing connector tests pin exact filenames
(`"Budget_Plans 2026.eml"`, `"ENG-1 Fix login bug.md"`, `"Invoice Q1-2026.md"`,
`"message-42.md"`) and must stay green unchanged.

## 2. Graph transport-exception mapping

**Problem:** raw `HttpRequestException` (DNS, TLS, socket reset) bypasses the `Result`
channel in all four Graph connectors, violating `FileContentProviderBase`'s documented
contract that HTTP errors are yielded as failures. Coverage is uneven: Exchange maps
`ODataError`/`ApiException`; SharePoint and OneDrive catch only `resyncRequired`/`itemNotFound`
and let everything else throw; **Microsoft Teams has no catch at all**.

**Design:**

- Add `public sealed record TransportFailed(Exception Inner) : RagError` to the closed union
  in `src/Rag.NET.Abstractions/Models/RagError.cs`. This breaks exhaustive `switch`
  expressions at compile time — which pre-1.0 is the desired outcome: the alternative is
  misreporting a DNS failure as an HTTP status. Every existing exhaustive match in the repo
  is updated.
- Map in all four Graph connectors: `HttpRequestException`, `TaskCanceledException` that is
  *not* caller cancellation (an HttpClient timeout), and `Azure.Identity`'s
  `AuthenticationFailedException` → `TransportFailed`.
- Fix the related bug: Kiota sets `ResponseStatusCode = 0` when there is no HTTP response, so
  `(HttpStatusCode)ex.ResponseStatusCode` currently produces an out-of-range value. When the
  status is 0, map to `TransportFailed` instead of `HttpFailed`.
- SharePoint, OneDrive and Teams gain the same eager-fetch helper shape Exchange and
  SharePoint already use, because C# forbids `yield return` inside a `catch`.
- Cancellation still propagates: `OperationCanceledException` when the caller's token is
  signalled is never converted to a failure (house posture).

**Deliberately unchanged:** the lazy `OpenContentAsync` delegates. They run inside the
ingestion pipeline, not the provider's enumeration, so they cannot become `Result` failures
at this layer; their exceptions are the pipeline's to handle.

**Testing:** per-connector tests injecting a handler that throws `HttpRequestException`,
asserting a `TransportFailed` failure rather than an escaping exception, and that a watermark
does not advance on transport failure (the existing Exchange `GraphError_MapsToResultFailure`
test is the template).

## 3. Embedded-message recursion (EML/MSG)

**Problem, as recorded:** embedded messages are warn-and-skipped; recursing them is the
natural follow-up.

**Problem, as found:** there is also a live unbounded-recursion hole.
`EmailAttachmentDispatcher` skips the dispatching parser by `ReferenceEquals` to prevent
self-recursion, which stops EML-in-EML — but an `.eml` containing a `.msg` containing an
`.eml` routes between *two different parser instances*, so the guard never fires. That chain
has no depth control today and is reachable from a crafted file. The depth counter this item
introduces is therefore a bug fix first and a feature second.

**Design:**

- New `EmailParserOptions` with `MaxEmbeddedDepth` (default 3) and `MaxEmbeddedMessages`
  (a total-node cap, default 50), configured through a new `AddEmailParser(...)` overload.
  The existing parameterless registration keeps working.
- Depth is carried in `DocumentMetadata.Tags` under a reserved key. This is the only channel
  that survives the public `IDocumentParser.ParseAsync(Stream, DocumentMetadata, ct)`
  boundary — the dispatcher hands off to an arbitrary `IDocumentParser`, so an
  assembly-internal mechanism cannot reach the child. The key is stripped before sections are
  emitted so it never leaks into stored chunk metadata.
- The `ReferenceEquals(self)` skip in the dispatcher is **replaced** by the depth check, so
  EML-in-EML now works and EML→MSG→EML now terminates. Exceeding the depth or node cap logs
  the existing warning (message updated to name the limit) and skips, rather than throwing —
  the parser's degraded-never-broken posture.
- EML and MSG need separate recursion implementations: nested messages arrive as live
  in-memory objects (`MessagePart.Message`, nested `Storage.Message`) owned by the parent's
  `using`, not as streams, so neither can re-enter the stream-based `ParseAsync`. Only the
  section-shaping is shared.
- Nested sections keep the parent's `DocumentId` (the dispatcher's existing behavior) and are
  distinguished by a composed `FileName`; `SectionIndex` continues to be stamped once at the
  top level, so recursion must not re-stamp.

**Testing:** an EML with an embedded EML yields the nested body; an MSG with a nested MSG
likewise; a chain deeper than `MaxEmbeddedDepth` stops with a warning; **an `.eml`→`.msg`→
`.eml` fixture terminates** (this test fails on today's code); the node cap triggers; and
nested attachments still dispatch to their own parsers.

## 4. PDF table dominance-guard refinement

**Problem:** `IsLayoutDominated` rejects any 2-3 column run of 8+ rows covering more than half
the page, so a full-page Key/Value table is missed by design (already documented as a known
limitation on the extractor class).

**Design:**

- `PassesPlausibilityGuards` already computes words-per-cell; carry that ratio on
  `DetectedTable` rather than recomputing it, since `IsLayoutDominated` has no access to the
  row data at its call site.
- `IsLayoutDominated` gains a fourth conjunct: runs averaging `<= 2.0` words per cell are
  exempt (dense Key/Value content, not prose columns).
- The threshold is tight by necessity. The existing `ThreeColumnPageLayout_NewsletterStyle`
  fixture sits at exactly 3 words per cell and must keep being rejected; anything looser than
  ~2.5 turns that test red. The exemption is strictly nested inside the existing `<= 4.0`
  window guard, so a run must already have passed that to reach it.

**Testing:** a new full-page Key/Value fixture (≤2 words per cell, 10+ rows, 2 columns) that
is now extracted — none exists today. The newsletter and two-column-prose fixtures must stay
green, and the new threshold constant is asserted tight enough that loosening it to 3.0
breaks the newsletter test.

## 5. Score-scale capability interface

**Problem:** `PersistentConversationMemory` filters recalled exchanges by
`PersistentMemoryOptions.MinScore` (default 0.7), applied client-side after the store
returns. Backed by `FederatedVectorStore`, whose RRF scores peak around 0.033 for two stores,
recall silently returns nothing — always. Azure AI Search's unbounded hybrid scores break the
same assumption from the other direction.

**Design:**

- New capability interface in `Rag.NET.Abstractions` declaring a store's score scale:
  similarity (comparable, roughly `[0,1]`, thresholdable) versus opaque ranking (ordinal
  only). It follows the existing capability-probe convention — a store implements it or it
  does not, and consumers test with `is`.
- `FederatedVectorStore` implements it directly, declaring opaque ranking. It deliberately
  does not federate capability interfaces, so delegation is not an option.
- `PersistentConversationMemory` probes the resolved store: on a similarity scale it applies
  `MinScore` as today; on an opaque scale it skips the threshold, takes top-K by rank, and
  logs a one-time warning naming the store type and the ignored option.
- Stores that do not implement the interface are treated as similarity — preserving today's
  behavior for every existing store, so this is additive.
- Azure AI Search declares opaque ranking (its hybrid scores are unbounded), which fixes the
  second half of the documented problem.

**Testing:** memory backed by a similarity store still filters by `MinScore`; memory backed by
a federated store recalls the top-K instead of silently nothing, and warns once (not per
call); a store not implementing the interface behaves exactly as before.

**Docs:** the known-limitation paragraphs in `vector-stores.md` and `retrieval.md` are
replaced with the actual behavior.

## 6. `ConfigureResilience` wiring

**Problem:** `RagBuilder.ConfigureResilience` registers a Polly pipeline named `"rag-net"`
that nothing resolves or executes. `observability.md:101` promises it wraps embedding and
vector-store calls; `observability.md:148` and `resilience.md:193` then retract it as a known
issue. The original design intended exactly the promised wiring; it was never implemented.

**Design:**

- Apply the pipeline through the repo's existing decorator machinery
  (`ServiceDecorationHelper`, as used by `UseFallbackChain`, `UseRateLimiting` and
  `UseCostBudgeting`) to `IEmbeddingGenerator` and `IVectorStore`. Both genuinely lack retry
  today — embedding providers have none, and Qdrant (gRPC), Pinecone (SDK) and PgVector
  (Npgsql) are not HTTP-typed clients.
- Decoration is applied only when `ConfigureResilience` is called, so the default DI graph is
  unchanged.
- **Double-retry is real and must be documented, not hidden:** Weaviate and Chroma configure
  `AddStandardResilienceHandler` on their own HTTP clients, so for those stores the decorator
  stacks on top of transport-level retries. The guide states this plainly and tells users to
  configure one layer or the other.
- Cancellation and `OperationCanceledException` pass through the pipeline untouched; the
  decorator must not convert a caller cancellation into a retry.

**Testing:** a failing-then-succeeding fake embedding generator is retried; a failing store
call is retried; cancellation is not retried; not calling `ConfigureResilience` leaves the
container graph unchanged (no decorator registered).

**Docs:** delete both "known issue" retractions and correct `observability.md:101` to describe
what now actually happens, including the double-retry note.

## Error handling summary

House posture throughout: connectors surface per-entry failures as `Result` failures;
parsers degrade rather than break (warn and skip); stores throw. Cancellation always
propagates and is never reclassified as a failure or retried.

## Out of scope

- Reserved Windows device names in the sanitizer (filenames here are metadata, not paths).
- URL-derived filenames in the Web/RSS/Sitemap providers.
- Mapping exceptions from lazy `OpenContentAsync` delegates into `Result` failures.
- Connector `FileHandle.Metadata` population — that is Phase 2.2, though it touches the same
  `ToHandle` methods, so 2.2 should follow 2.1 to avoid conflicting edits.
