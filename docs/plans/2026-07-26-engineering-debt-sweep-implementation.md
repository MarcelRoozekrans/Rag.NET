# Engineering Debt Sweep Implementation Plan (Phase 2.1)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Clear the six review-cycle debts recorded in `docs/planning/ROADMAP.md` — a shared filename sanitizer, transport-exception mapping in the Graph connectors, embedded-message recursion (which also closes a live unbounded-recursion hole), a PDF table dominance-guard exemption, score-scale-aware memory recall, and wiring the dangling `ConfigureResilience` pipeline.

**Architecture:** Per `docs/plans/2026-07-26-engineering-debt-sweep-design.md`. Six independent parts sharing no code — implement in the listed order only because Parts A and C are the ones that fix real bugs, and Part A touches `ToHandle` methods that Phase 2.2 will edit next.

**Tech Stack:** .NET 10, xUnit v3, NSubstitute (hand-written fakes for `ValueTask` members — EPS06), MimeKit (EML), MsgReader (MSG), PdfPig, Polly via `Microsoft.Extensions.Resilience`, Microsoft.Graph 5.x.

**Conventions:** MA0051 (≤60-line methods), MA0015 (ArgumentException paramName), ZA0601/ZA0501 (no LINQ/boxing in hot loops), EPS05/EPS06, HLQ012/HLQ013 — all warnings-as-errors, build must end 0/0. LoggerMessage source-gen for all logging. xUnit v3 `TestContext.Current.CancellationToken` in every await; deterministic tests, no sleeps. Conventional commits ending with a blank line then `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. **NEVER stage `.lucent/chunks.json` or `.lucent/embeddings.bin`** — always `git add` explicit paths, never `-A`/`.`.

**Read before starting any part:** the design doc, and for the part you are on, the files named in its Investigation notes. Every line reference below was verified on 2026-07-26 at commit 44ce23f — re-verify before editing, since earlier parts may have shifted lines.

---

## Part A — Shared filename sanitizer

### Task A1: the helper + its tests

**Files:**
- Create: `src/Rag.NET.DataProviders/FileNameSanitizer.cs`
- Create: `tests/Rag.NET.DataProviders.Tests/FileNameSanitizerTests.cs`

Read `src/Rag.NET.DataProviders/FileExtensionMatcher.cs` first for the house shape of a shared static helper in this project. Note it is `internal` — **this one must be `public`**: each connector is a separate assembly and NuGet package, and `Rag.NET.DataProviders.csproj:15-19` grants `InternalsVisibleTo` only to the test project.

**API:**

```csharp
public static string Sanitize(string? value, string fallback, int maxLength = 128)
```

Behavior, in order: null/whitespace input → `fallback` (itself sanitized); replace every char in the deterministic invalid set with `'_'`; trim leading/trailing whitespace; trim trailing dots; if the result is now empty → `fallback`; truncate the stem to `maxLength`.

The invalid set is **pinned, not `Path.GetInvalidFileNameChars()`** — that method returns 41 chars on Windows and 2 on Linux, so filenames currently vary by host, and it clones an array per call. Use a cached `SearchValues<char>` over the Windows superset: `< > : " / \ | ? *` plus the C0 control range U+0000 through U+001F.

Document on the class that reserved device names (`CON`, `PRN`, `NUL`, `COM1`…) are deliberately **not** handled: `FileHandle.FileName` is metadata for parser selection and display, never a filesystem path.

**Tests** (write these first, watch them fail):

```csharp
// 1. Sanitize_InvalidChars_ReplacedWithUnderscore — "a/b:c*d" => "a_b_c_d"
// 2. Sanitize_ControlChars_Replaced — "a" + (char)0x01 + "b" => "a_b"
// 3. Sanitize_NullOrWhitespace_ReturnsFallback — null, "", "   "
// 4. Sanitize_AllInvalid_ReturnsFallback — "///" => fallback
// 5. Sanitize_TrimsWhitespaceAndTrailingDots — "  name.  " => "name"
// 6. Sanitize_TruncatesToMaxLength
// 7. Sanitize_FallbackIsAlsoSanitized — fallback "bad/name" => "bad_name"
// 8. Sanitize_IsHostIndependent — asserts the pinned set covers every char in
//    Path.GetInvalidFileNameChars() on THIS host, so a Linux run cannot be laxer
//    than a Windows one. (This is the regression that motivated the pinned set.)
// 9. Sanitize_ValidName_Unchanged — "Invoice Q1-2026" round-trips
```

**Commit:** `feat(data-providers): shared FileNameSanitizer with a host-independent invalid set`

### Task A2: adopt in the three connectors that have copies

**Files:**
- Modify: `src/Rag.NET.DataProviders.Exchange/ExchangeMailDataProvider.cs` — delete `SanitizeFileName` (~:299-307), call the helper at the `FileName` site (~:266-268). The fallback `message-{messageId}` now goes **through** the sanitizer (today it bypasses it).
- Modify: `src/Rag.NET.DataProviders.Linear/LinearDataProvider.cs` — delete `SanitizeFileName` (~:331-339), call the helper (~:259) with fallback `issue-{identifier}`.
- Modify: `src/Rag.NET.DataProviders.Gmail/GmailDataProvider.cs` — replace the inlined loop in `ToHandle` (~:92-110) with a helper call, fallback `message-{uid}`.

These existing assertions must stay green **unchanged** — if one goes red, the helper is wrong, not the test:
- `tests/Rag.NET.DataProviders.Exchange.Tests/ExchangeMailDataProviderTests.cs:48` → `"Quarterly Report.eml"`; `:481-482` → `"Budget_Plans 2026.eml"`, `"message-msg-2.eml"`
- `tests/Rag.NET.DataProviders.Linear.Tests/LinearDataProviderTests.cs:93` → `"ENG-1 Fix login bug.md"`
- `tests/Rag.NET.DataProviders.Gmail.Tests/GmailDataProviderTests.cs:76` → `"Invoice Q1-2026.md"`; `:164` → `"message-42.md"`; `:178-180` assert no `/` or `\`

**Commit:** `refactor(data-providers): adopt FileNameSanitizer in Exchange, Linear, and Gmail`

### Task A3: adopt in the six connectors that sanitize nothing

**Files** (each is a one-line `FileName:` change plus a fallback):
- `src/Rag.NET.DataProviders.Confluence/ConfluenceDataProvider.cs:156` — `$"{p.Title}.md"`
- `src/Rag.NET.DataProviders.Asana/AsanaDataProvider.cs:97` — `$"{task.Name}.md"`
- `src/Rag.NET.DataProviders.Notion/NotionDataProvider.cs:97` — `$"{title}.md"`
- `src/Rag.NET.DataProviders.Airtable/AirtableDataProvider.cs:103` — `$"{GetRecordTitle(record)}.md"`
- `src/Rag.NET.DataProviders.Slack/SlackDataProvider.cs:102` — `$"{channel.Name}-{dateStr}.md"`
- `src/Rag.NET.DataProviders.MicrosoftTeams/MicrosoftTeamsDataProvider.cs:163` — `$"{channelName}-{dateStr}.md"`

Sanitize only the user-controlled portion — the date suffix in Slack/Teams is generated and safe. Use an id-derived fallback per connector (e.g. `page-{id}`, `task-{gid}`).

Add **one** test per connector proving a hostile name is sanitized, in that connector's existing test project (e.g. a Teams channel named `Design/Review: Q1` → `Design_Review_ Q1-2026-01-01.md`). Follow each project's existing fake/stub pattern; do not introduce a new one.

**Commit:** `fix(data-providers): sanitize synthesized filenames in six connectors`

---

## Part B — Graph transport-exception mapping

### Task B1: the `RagError.TransportFailed` case

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/RagError.cs` — add `public sealed record TransportFailed(Exception Inner) : RagError;` with an XML doc distinguishing it from `HttpFailed` (no HTTP response was received) and from `StorageFailed` (which is about storage operations).

This is a **compile-time break** for exhaustive `switch` expressions on `RagError` — intended. Build the solution and fix every resulting error; `grep -rn "RagError\." src/ tests/ --include=*.cs` finds the match sites. Do not add a `_ =>` discard to silence them; handle the new case explicitly so future cases keep breaking loudly.

**Commit:** `feat(abstractions): add RagError.TransportFailed for pre-HTTP failures`

### Task B2: map in all four Graph connectors

**Files:**
- `src/Rag.NET.DataProviders.Exchange/ExchangeMailDataProvider.cs:246-261` — has `ODataError`/`ApiException` catches; add the transport catches, and **fix the status-0 bug**: Kiota sets `ResponseStatusCode = 0` when there is no response, so `(HttpStatusCode)ex.ResponseStatusCode` produces an out-of-range value. When it is 0, return `TransportFailed`, not `HttpFailed`.
- `src/Rag.NET.DataProviders.SharePoint/SharePointDataProvider.cs:120-134` — keeps its `resyncRequired`/`itemNotFound` → full-traversal fallback; everything else now maps instead of throwing.
- `src/Rag.NET.DataProviders.OneDrive/OneDriveDataProvider.cs:135-150` — same.
- `src/Rag.NET.DataProviders.MicrosoftTeams/MicrosoftTeamsDataProvider.cs` — **no catches at all today**; `GetTeamsAsync` (~:65), `GetChannelsAsync` (~:84), `FetchMessagesAsync` (~:104).

Catch and map: `HttpRequestException`, `TaskCanceledException` **when the caller's token is not signalled** (an HttpClient timeout), and `Azure.Identity.AuthenticationFailedException`.

Cancellation must still propagate — `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` ordered **before** the transport catches, since `TaskCanceledException` derives from it.

C# forbids `yield return` inside `catch`, so SharePoint, OneDrive and Teams need the eager-fetch helper shape Exchange already uses (`SharePointDataProvider.cs:72-75` documents the pattern).

**Tests** — one per connector, using each project's existing `FakeGraphHandler`-style HTTP stub (read `tests/Rag.NET.DataProviders.Exchange.Tests/FakeGraphHandler.cs`):

```csharp
// 1. Transport failure (handler throws HttpRequestException) => Result failure of type
//    RagError.TransportFailed, NOT an escaping exception.
// 2. Exchange only: a Kiota ApiException with ResponseStatusCode 0 => TransportFailed
//    (today: HttpFailed with an out-of-range status).
// 3. Exchange only: the watermark does NOT advance on transport failure — mirror the
//    existing GraphError_MapsToResultFailure test at ExchangeMailDataProviderTests.cs:392-411.
// 4. Caller cancellation still throws OperationCanceledException (not converted).
```

**Commit:** `fix(data-providers): map Graph transport failures into the Result channel`

---

## Part C — Embedded-message recursion (and the recursion hole)

Read `docs/plans/2026-07-26-engineering-debt-sweep-design.md` §3 before starting. **This part fixes a live bug**: `EmailAttachmentDispatcher.cs:30` skips the dispatching parser by `ReferenceEquals`, which stops EML-in-EML but *not* an `.eml` → `.msg` → `.eml` chain, because those route between two different parser instances. That chain is unbounded today.

### Task C1: the failing test that proves the hole

**Files:**
- Create/modify: `tests/Rag.NET.Parsers.Email.Tests/EmbeddedMessageRecursionTests.cs`

Build a fixture: an `.eml` whose attachment is an `.msg` whose attachment is an `.eml` (nest ~10 deep, or as deep as is practical to generate programmatically). Assert parsing **completes** within a bounded time and does not stack-overflow.

Run it and confirm it fails (or hangs/overflows) on current `main`. Record the observed failure in the commit message — this is the evidence the bug was real.

### Task C2: options + depth plumbing

**Files:**
- Create: `src/Rag.NET.Parsers.Email/EmailParserOptions.cs` — `MaxEmbeddedDepth = 3`, `MaxEmbeddedMessages = 50`. Mutable POCO, validated in the DI extension (house convention — see any `src/Rag.NET.Abstractions/Models/Options/*.cs`).
- Modify: `src/Rag.NET.Parsers.Email/EmailParserBuilderExtensions.cs:19-32` — new `AddEmailParser(Action<EmailParserOptions>?)` overload; the existing parameterless registration keeps working.
- Modify: `src/Rag.NET.Parsers.Email/EmailAttachmentDispatcher.cs` — **replace** the `ReferenceEquals(self)` skip (~:30) with a depth check.

Depth travels in `DocumentMetadata.Tags` under a reserved key (e.g. `"__rag_email_depth"`). This is the only channel that survives the public `IDocumentParser.ParseAsync(Stream, DocumentMetadata, ct)` boundary — the dispatcher hands off to an arbitrary `IDocumentParser`, so an assembly-internal mechanism cannot reach the child. **Strip the key before emitting sections** so it never reaches stored chunk metadata; add a test asserting that.

**Commit:** `fix(parsers): bound email attachment recursion with a depth limit`

### Task C3: recurse into embedded messages

**Files:**
- Modify: `src/Rag.NET.Parsers.Email/EmailDocumentParser.cs:58-74` — replace the `MessagePart` warn-and-skip with recursion into `embedded.Message`.
- Modify: `src/Rag.NET.Parsers.Email/MsgDocumentParser.cs:93-113` — same for nested `Storage.Message`.
- Modify: `src/Rag.NET.Parsers.Email/EmailParserLog.cs:5-9` — update the warning to name the limit that was hit (it currently says "not yet recursed", which stops being true).

The two cannot share a code path: nested messages arrive as **live in-memory objects** (`MessagePart.Message`, nested `Storage.Message`) owned by the parent's `using`, not as streams, so neither can re-enter the stream-based `ParseAsync`. Factor out the section-shaping only.

Nested sections keep the parent's `DocumentId` (the dispatcher's existing behavior at `EmailAttachmentDispatcher.cs:47`) and are distinguished by a composed `FileName` — sanitize it with `FileNameSanitizer` from Part A. `SectionIndex` is stamped once at the top level (`EmailDocumentParser.cs:47-50`, `MsgDocumentParser.cs:52-56`); recursion must **not** re-stamp.

Exceeding either limit logs the warning and skips — degraded-never-broken, never throws.

**Tests:**

```csharp
// 1. Eml_WithEmbeddedEml_YieldsNestedBody (works for the first time — the self-skip blocked it)
// 2. Msg_WithNestedMsg_YieldsNestedBody
// 3. DepthExceeded_WarnsAndSkips — nest MaxEmbeddedDepth+1, assert the warning and that
//    outer content still parses
// 4. NodeCapExceeded_WarnsAndSkips
// 5. The C1 alternating-chain test now passes
// 6. NestedAttachments_StillDispatchToOwnParsers — a PDF inside an embedded EML
// 7. DepthTag_NotLeakedIntoSectionMetadata
```

**Commit:** `feat(parsers): recurse into embedded EML and MSG messages`

---

## Part D — PDF table dominance-guard exemption

**Files:**
- Modify: `src/Rag.NET.Parsers.Pdf/TableExtraction/DetectedTable.cs` — carry the words-per-cell ratio.
- Modify: `src/Rag.NET.Parsers.Pdf/TableExtraction/PdfTableExtractor.cs` — `PassesPlausibilityGuards` (~:260-283) already computes the ratio; store it instead of discarding it. `IsLayoutDominated` (~:294-297) gains a fourth conjunct exempting runs at `<= 2.0` words per cell. Add the constant next to the existing ones (~:32-64).
- Modify: `tests/Rag.NET.Parsers.Pdf.Tests/PdfTableExtractorTests.cs`

`IsLayoutDominated` has no access to row data at its call site (~:183), which is why the ratio rides on `DetectedTable` rather than being recomputed.

**The threshold is tight by necessity.** `ThreeColumnPageLayout_NewsletterStyle_NoTables` (~:253-276) sits at exactly **3 words per cell** and must keep being rejected — anything looser than ~2.5 turns it red. `TwoColumnPageLayout_WideCenterGutter_NoTables` (~:219-239) is at 5 words/cell and is rejected earlier by the `<= 4.0` window guard, so it is unaffected.

**Tests:**

```csharp
// 1. FullPageKeyValueTable_IsExtracted — NEW fixture: 2 columns, 12+ rows, <=2 words per
//    cell, spanning the whole page. No such fixture exists today. This is the rescue case.
// 2. ThreeColumnPageLayout_NewsletterStyle_NoTables — unchanged, must stay green.
// 3. TwoColumnPageLayout_WideCenterGutter_NoTables — unchanged, must stay green.
// 4. A fixture at ~2.5 words/cell stays rejected, pinning the exemption tight.
```

**Commit:** `fix(parsers): exempt dense key/value runs from the PDF layout-dominance guard`

---

## Part E — Score-scale capability interface

**Files:**
- Create: `src/Rag.NET.Abstractions/Abstractions/` — a capability interface declaring a store's score scale (similarity, comparable and roughly `[0,1]`, thresholdable; versus opaque ranking, ordinal only). Read `ISparseSearchable.cs` and `IHybridSearchable.cs` first and match their doc style; the repo's convention is a capability probe (`store is IFoo`), not a property on `IVectorStore`.
- Modify: `src/Rag.NET/Storage/FederatedVectorStore.cs` — implement it, declaring **opaque ranking**. Its RRF scores peak near `2/61 ≈ 0.033` for two stores (`:155-196`). It deliberately does not federate capability interfaces (`:20-22`), so it must implement this itself.
- Modify: `src/Rag.NET.VectorStores.AzureAISearch/AzureAISearchVectorStore.cs` — declare opaque ranking (its hybrid scores are unbounded).
- Modify: `src/Rag.NET.Memory/PersistentConversationMemory.cs:22-42` — probe the resolved store; on a similarity scale apply `MinScore` as today; on an opaque scale skip the threshold, take top-K by rank, and log a **one-time** warning naming the store type and the ignored option. Add a LoggerMessage entry.

Stores that do not implement the interface are treated as **similarity** — preserving today's behavior for every existing store, so this is additive.

**Tests** (`tests/Rag.NET.Tests/Memory/`):

```csharp
// 1. SimilarityStore_AppliesMinScore — unchanged behavior
// 2. OpaqueStore_SkipsMinScore_RecallsTopK — today this silently returns nothing
// 3. OpaqueStore_WarnsOnce — two recalls, exactly one warning
// 4. StoreWithoutCapability_BehavesAsSimilarity
// 5. FederatedVectorStore declares opaque; a similarity store does not implement the interface
```

**Docs:** replace the known-limitation paragraphs at `docs/guide/vector-stores.md:573` and the related note at `docs/guide/retrieval.md:158` with the actual behavior.

**Commit:** `feat(memory): make recall score-scale aware instead of silently returning nothing`

---

## Part F — `ConfigureResilience` wiring

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs:276-308` — keep the `"rag-net"` registration, then decorate `IEmbeddingGenerator` and `IVectorStore` with it. Delete the `KNOWN ISSUE` XML-doc paragraph.
- Read first: `ServiceDecorationHelper` and one existing consumer (`UseFallbackChain`, `UseRateLimiting`, or `UseCostBudgeting`) — it is lifetime-preserving and uses GUID-keyed inner registrations to avoid a container deadlock. Do not hand-roll decoration.
- Create the two decorator types alongside the existing resilience decorators.

Decorate **only when `ConfigureResilience` is called**, so the default DI graph is unchanged.

Cancellation must pass through untouched — a caller's `OperationCanceledException` must never be retried. Polly retries on exceptions by default; configure the predicate to exclude it.

**Tests** (`tests/Rag.NET.Tests/DependencyInjection/`):

```csharp
// 1. EmbeddingGenerator_TransientFailure_IsRetried — fake fails twice then succeeds
// 2. VectorStore_TransientFailure_IsRetried
// 3. Cancellation_IsNotRetried — assert exactly one attempt
// 4. WithoutConfigureResilience_NoDecoratorRegistered — the graph is untouched
// 5. Decoration preserves the registered lifetime (the ServiceDecorationHelper contract)
```

**Docs — all three must change together:**
- `docs/guide/observability.md:101` — correct it to describe what now happens.
- `docs/guide/observability.md:148` — delete the retraction.
- `docs/guide/resilience.md:193-195` — delete the "Known issue" section.
- Add the **double-retry** note: Weaviate and Chroma hand-build a retry-only `ResilienceHandler` on their own HTTP clients (`AddRetry(new HttpRetryStrategyOptions())`, *not* `AddStandardResilienceHandler`), so for those stores the decorator stacks on top of transport-level retries. Tell users to configure one layer or the other. Do not hide this.

**Commit:** `feat(resilience): wire ConfigureResilience into embedding and vector-store calls`

---

## Final verification

1. `dotnet build Rag.NET.slnx` → 0 warnings / 0 errors.
2. Every touched test project green, plus full `tests/Rag.NET.Tests` (~1202) as a regression net.
3. `docs/planning/ROADMAP.md` — remove the six now-closed entries from "Recorded follow-up debts"; `docs/planning/MILESTONE.md` — mark Phase 2.1 complete. (Do this at close-out, after the whole-phase review, not per part.)
4. Whole-phase review; merge decision.
