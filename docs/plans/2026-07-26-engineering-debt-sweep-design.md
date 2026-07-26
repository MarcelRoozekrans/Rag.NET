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

**Recorded, not fixed:** `src/Rag.NET.Api/Webhooks/GenericWebhookPayloadParser.cs:77` builds
`$"{documentId}.txt"` straight from an untrusted webhook payload with no sanitization. It is a
tenth synthesizing site, but in a different assembly (`Rag.NET.Api`, which does not reference
`Rag.NET.DataProviders`) and outside this phase's connector scope. Found during the Part A
review; noted here so it is not rediscovered as new.

**Recorded, not fixed (added during Part C):** Part C introduced a *fourth* sanitizer —
`EmbeddedMessageMetadata.Sanitize` in `src/Rag.NET.Parsers.Email/` — for the composed file name
of an embedded message. `Rag.NET.Parsers.Email` does not reference `Rag.NET.DataProviders`, and
adding a package dependency from a parser to the connector assembly for one call site was
judged worse than a local copy; this section's own premise is that the copies are the problem,
so the decision is recorded rather than buried. Reviewed against `FileNameSanitizer` and found
behaviourally consistent (same pinned invalid set, trimming, trailing dots, length cap,
surrogate guard) except for three divergences: (1) it does not collapse an all-replacement
result to the fallback — `"///"` yields `"___"` here and `"embedded-message"` there; (2) the
length cap differs — `FileNameSanitizer`'s `maxLength` defaults to 128, while
`EmbeddedMessageMetadata.MaxNameLength` is 64, so the same subject truncates at different
points; (3) post-truncation trimming differs — `FileNameSanitizer.TrimEdges` re-trims to a
fixed point (trailing dots and whitespace re-expose each other) and treats all
`char.IsWhiteSpace` characters as whitespace, while `EmbeddedMessageMetadata` truncates *after*
its single `Trim()` and then applies `TrimEnd('.', ' ')`, which will not remove a re-exposed
non-breaking space (U+00A0) or tab. All three are cosmetic — the name is display and
provenance metadata, never a path — but the eventual unification must reconcile them rather
than assume a single behavioural gap. The right fix is to move `FileNameSanitizer` somewhere both
assemblies can reference (`Rag.NET.Abstractions` is the obvious home) and delete both copies,
which is a package-layout change and not this phase's business.

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

- Add `public sealed record TransportFailed(Exception Inner) : RagError` in
  `src/Rag.NET.Abstractions/Models/RagError.cs`. The reason is honest modelling, not
  compile-time enforcement: a DNS failure has no HTTP status, so reporting one is a lie, and
  the only value available to lie with — `(HttpStatusCode)0` — is not even a valid status.
  Every existing match on `RagError` is reviewed and updated to handle the new case
  deliberately.

  > **Corrected after implementation.** An earlier revision of this bullet claimed the new case
  > "breaks exhaustive `switch` expressions at compile time", and leaned on that break as the
  > mechanism that keeps matches honest. That is wrong, twice over:
  >
  > - C# does no closed-hierarchy exhaustiveness analysis on reference types. A `switch`
  >   expression over `RagError` with no `_` arm does not enumerate the subtypes; it emits
  >   **CS8509** ("the switch expression does not handle all possible values") and, under this
  >   repo's warnings-as-errors, fails the build outright. A discard-free match is therefore
  >   not an exhaustiveness check — it is a standing build break.
  > - `RagError` is not a closed union at all. It is an ordinary `public abstract record` with
  >   no `private protected` constructor, so any external assembly can derive from it.
  >
  > In practice the repo's only two matches both already carried a `_` arm, so adding the case
  > broke nothing and nothing had to be fixed to make the build pass. Whether to actually close
  > the union (a `private protected` constructor — a public-API decision with its own
  > extensibility trade-off) is deliberately **out of scope for this phase**.
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

**Correction (measured during implementation, 2026-07-26; mechanism corrected after review).**
The paragraph above was written from reading the dispatcher, and the Part C test that was
supposed to prove a crash did not fail. Measured on the pre-fix code, the *absence of a bound*
is confirmed — an alternating chain parsed every level to depth 56,
`sections == 2 × (depth + 1)` throughout, so the `ReferenceEquals` guard genuinely never
fires. But the pre-fix consequence is **resource amplification, not a crash**, and the reason
is the fixture's size growth, *not* anything about async iterators:

- **The recursion is stack-recursive.** An earlier revision of this paragraph claimed "async
  iterators unwind per `await` and never approach the stack limit". That is **false**, and it
  is the exact reasoning someone would use to justify raising `MaxEmbeddedDepth` into a crash.
  Each nesting level adds frames that are not unwound until the nested enumeration finishes.
  Measured against the shipped parser with the depth bound raised: **480 levels survive; 500+
  terminate the process with exit code `-1073741571` (`0xC00000FD`,
  `STATUS_STACK_OVERFLOW`)** — uncatchable, so the degraded-never-broken posture cannot help.
- **Depth is cheap to craft, if something lets you reach it.** Hand-written raw MIME costs
  ~81 bytes per level, so ~500 levels is ~40 KB. The Part C fixtures used MimeKit's writer and
  cost ~1.18× *multiplicatively* per level (base64 on the EML wrap, raw in the CFB wrap), which
  is why 56 levels needed 124 MB there and the crash depth looked unreachable. MimeKit's own
  `MimeMessage.LoadAsync` parses 5,000 levels from a 404 KB file without trouble — its parser
  is iterative, so it is not the limiting factor; this parser's traversal is.
- **Both the pre-fix path and the shipped defaults are nonetheless safe.** Pre-fix, the only
  reachable chains were the multiplicative ones, so the crash depth cost hundreds of MB. Post-
  fix, `MaxEmbeddedDepth = 3` means the same 404 KB / 5,000-level file yields 4 sections and no
  crash. What was missing was a ceiling on the *option*: `MaxEmbeddedDepth` is now capped at
  `EmailParserOptions.MaxSupportedEmbeddedDepth = 64` (21× the default, an order of magnitude
  below the measured floor), rejected at `AddEmailParser` time with the measurement in the
  exception message. Without that cap the option was a documented "safety bound" that silently
  became a process-kill primitive when raised.

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
- ~~Azure AI Search declares opaque ranking (its hybrid scores are unbounded), which fixes the
  second half of the documented problem.~~ **Corrected during implementation — Azure AI Search
  was evaluated and deliberately left as similarity (it does not implement the interface).**
  The original premise was wrong: `PersistentConversationMemory` only ever calls
  `IVectorStore.SearchAsync`, never `HybridSearchAsync`, and `AzureAISearchVectorStore.SearchAsync`
  issues a pure vector query (`searchText: null`), whose `@search.score` is a bounded monotone
  function of the similarity metric (~0.333–1.0 for cosine) and is therefore thresholdable.
  Declaring it opaque would have fixed nothing — that path already worked — while regressing
  existing Azure users, for whom `MinScore` would silently stop applying and every turn would
  inject up to `TopK` past exchanges regardless of relevance. The governing rule:
  `IScoreScaleAware` describes the scale of `IVectorStore.SearchAsync`, the interface it sits
  on. Azure's is similarity; Federated's is RRF.
- Recorded, not fixed: Azure AI Search's *hybrid* scores (`HybridSearchAsync`) are positive and
  unbounded, so a fixed cross-backend cut-off remains meaningless there. No consumer currently
  thresholds them with one, and the capability interface does not describe that path.

**Testing:** memory backed by a similarity store still filters by `MinScore`; memory backed by
a federated store recalls the top-K instead of silently nothing, and warns once (not per
call); a store not implementing the interface behaves exactly as before.

**Docs:** the known-limitation paragraphs in `vector-stores.md` and `retrieval.md` are
replaced with the actual behavior, along with the same claim where users actually meet it:
the `UseFederatedSearch` `<remarks>`, the `PersistentMemoryOptions.MinScore` XML doc, and
`memory.md`'s options table, flow diagram, and behavior table.

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
- **Double-retry is real and must be documented, not hidden:** Weaviate and Chroma hand-build a
  retry-only `ResilienceHandler` on their own HTTP clients (a bare
  `AddRetry(new HttpRetryStrategyOptions())` pipeline — *not* `AddStandardResilienceHandler`,
  so no transport-level timeout, circuit breaker or concurrency limiter), so for those stores
  the decorator stacks on top of transport-level retries. Both layers default to
  `MaxRetryAttempts = 3`, which Polly counts as retries — 4 attempts per layer, 16 requests
  worst case. The guide states this plainly and tells users to configure one layer or the other.
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
