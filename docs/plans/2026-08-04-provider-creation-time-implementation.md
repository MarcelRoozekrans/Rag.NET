# Provider Creation Time Implementation Plan (Phase 4.9)

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Stop provider-ingested documents claiming they were created at ingestion time, and make the timestamps connectors already emit actually drive time-weighted retrieval.

**Architecture:** `DocumentMetadata.CreatedAt` becomes nullable with no default, so nothing is fabricated; `MetadataBehavior` writes the `created_at` tag only when a value exists; `TimeWeightedOptions.FallbackMetadataKeys` — built but wired to nothing — gains defaults matching tags seven connectors already write.

**Tech Stack:** .NET 10, xUnit v3.

**Design:** `docs/plans/2026-08-04-provider-creation-time-design.md`

---

## The measured change surface

`DocumentMetadata.CreatedAt` is read in exactly **four** places in `src/`:

| Site | What it does | Effect of nullable |
|---|---|---|
| `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs:20` | `ctx.Metadata.CreatedAt.ToString("O")` | **Must become conditional** — the only real change |
| `src/Rag.NET.Abstractions/Containers/ContainerContext.cs:103` | `CreatedAt = metadata.CreatedAt` | Pass-through, compiles unchanged |
| `src/Rag.NET.Abstractions/Containers/ContainerEntryDispatcher.cs:79` | `CreatedAt = metadata.CreatedAt` | Pass-through, compiles unchanged |
| `src/Rag.NET.Parsers.Email/EmbeddedMessageMetadata.cs:34` | `CreatedAt = parent.CreatedAt` | Pass-through, compiles unchanged |

`TimeWeightedRetriever` reads the **metadata key** `created_at`, not the property, so the type change does not touch it. The `CreatedAt` members on `LinearComment` and `ZendeskComment` are unrelated types — do not touch them.

Test files that assume a non-null `DateTime`: `tests/Rag.NET.Tests/Ingestion/MetadataBehaviorCreatedAtTests.cs`, `tests/Rag.NET.Tests/Retrieval/TimeWeightedRetrieverTests.cs`, `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`, `tests/Rag.NET.DataProviders.Tests/MetadataContractTests.cs`.

## Ground rules

- Warnings are errors. **No `#pragma`, `SuppressMessage`, `NoWarn`, `TreatWarningsAsErrors=false`.** MA0051 (≤60-line methods), MA0048, ERP022, EPC12/13, ZA0601.
- xUnit v3, `TestContext.Current.CancellationToken`, no sleeps.
- Conventional commits with bodies, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Never `git add -A`** — explicit paths. **Never pipe build/test output through `head`/`tail`/`grep`.**
- A file watcher edits `.csproj` concurrently — `git status` before committing.
- **This phase changes behaviour deliberately.** Where an existing test asserts the fabricated timestamp, update it as a stated decision — do not adjust assertions until green.

**Baselines:** `Rag.NET.Tests` 1151, `RepoConventions` 36 + 1 skip, `PackageValidation` 20, `DataProviders.Tests` (measure before changing).

**Branch:** `fix/provider-creation-time`.

---

## Task 1: The missing test — write it first, watch it fail

**The absence of this test is the story.** `IngestFromProviderTests` only checks that a connector *emitting* a `created_at` tag throws; the two ends are tested in isolation and the path between them never is. Write it before any production change.

**Files:**
- Modify: `tests/Rag.NET.Tests/DataProviders/IngestFromProviderTests.cs`

**Step 1: Write the failing test.**

Model it on the existing fixtures in that file (there is already a fake provider harness — reuse it, do not invent a second one).

```csharp
[Fact]
public async Task IngestFromProvider_WithNoTimestampFromTheConnector_DoesNotFabricateACreationTime()
{
    // A provider that supplies no timestamp must not produce a created_at that
    // TimeWeightedRetriever will read as "this document is brand new". Before Phase 4.9
    // DocumentMetadata.CreatedAt defaulted to DateTime.UtcNow, so every provider-ingested
    // document ranked as ingested-now regardless of its real age.
    var chunks = await IngestAndCaptureChunksAsync(/* fake provider with one entry */);

    Assert.DoesNotContain(ReservedMetadataKeys.CreatedAt, chunks[0].Metadata.Keys);
}
```

**Step 2: Run it and confirm it FAILS**, with `created_at` present and holding roughly now:

```bash
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~DoesNotFabricateACreationTime"
```

**Report the actual failure message.** If it passes, the test is not reaching the defect — fix the test, not the product.

**Step 3: Commit the failing test**, or hold it and commit together with Task 2 if a red test on the branch is awkward. **Say which you chose.**

---

## Task 2: Stop fabricating

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/DocumentMetadata.cs:22`
- Modify: `src/Rag.NET/Ingestion/Behaviors/MetadataBehavior.cs:20`

**Step 1:** Make the property nullable and drop the default:

```csharp
public DateTime? CreatedAt { get; init; }
```

Update its XML doc: it no longer defaults to now, and an absent value means *unknown*, which `TimeWeightedRetriever` treats neutrally.

**Step 2:** Make the write conditional:

```csharp
if (ctx.Metadata.CreatedAt is { } createdAt)
{
    chunk.Metadata.TryAdd(ReservedMetadataKeys.CreatedAt, createdAt.ToString("O"));
}
```

Keep it inside the existing per-chunk loop, and keep `TryAdd` — a connector tag must still win, which is what `ReservedMetadataKeys`' own doc comment describes.

**Step 3: Build.** The three pass-through sites should compile untouched. **If any does not, report it** — that would mean the surface table above is wrong.

**Step 4: Run Task 1's test — it must now PASS.**

**Step 5: Run the full suite.** `MetadataBehaviorCreatedAtTests` and `MetadataContractTests` will likely fail where they assert the fabricated value. **Update them to assert the new, honest behaviour and state each change** — an assertion changed without a stated reason is how a behaviour regression hides.

**Step 6: Commit.** Mark the breaking change in the body: `DocumentMetadata.CreatedAt` is now `DateTime?`; consumers reading it as non-null must handle null. Nothing is published, so no shims are needed — but say so explicitly.

---

## Task 3: Make the batch-level override real

**Files:**
- Modify: `src/Rag.NET/DataProviders/RagPipelineExtensions.cs` (the `BuildMetadata` return, ~line 322-328)

`ContentType` is copied from `baseMetadata`; `CreatedAt` is silently dropped.

**Step 1: Write a test** asserting that a caller-supplied `baseMetadata.CreatedAt` reaches the ingested document. It fails today.

**Step 2:** Add `CreatedAt = baseMetadata?.CreatedAt,` to the returned `DocumentMetadata`.

**Step 3:** Test passes.

**Be precise in the commit body about what this is and is not.** `baseMetadata` is supplied **once per `IngestFromProviderAsync` call**, not per document — so this makes a batch-level override work, and does **not** give any document its own real creation time. Overstating it here would re-create exactly the "one copied property" misunderstanding this phase exists to correct.

---

## Task 4: Wire the fallback that already exists

**Files:**
- Modify: `src/Rag.NET/Models/Options/TimeWeightedOptions.cs` (find it: `FallbackMetadataKeys`, currently `= []`)

**Step 1: Write tests first** — a chunk carrying only `updated_at` must get a real decay rather than the neutral 1.0; likewise `published_at` and `lastmod`; and with several present, the documented order wins.

**Step 2:** Change the default:

```csharp
public IReadOnlyList<string> FallbackMetadataKeys { get; init; } =
    ["updated_at", "published_at", "lastmod"];
```

**Step 3:** Update the XML doc to say which connectors these cover — **Asana, Jira, Notion, Zendesk (tickets and articles), RSS, Sitemap, Exchange already write them** — and that `date` is deliberately excluded: Gmail's is a full timestamp while Slack's and Teams' are day-granularity, and the key is generic enough that a user's own metadata may mean something else by it. Document adding `"date"` as a one-line opt-in.

**Step 4:** Verify the claim rather than trusting the design doc. **Grep the named connectors for those tag writes and confirm each.** If any does not write the tag it is credited with, **say so and correct the list** — this is exactly the class of claim this repository keeps finding wrong.

---

## Task 5: Pin the property the design rests on

**Files:**
- Modify: `tests/Rag.NET.Tests/Retrieval/TimeWeightedRetrieverTests.cs`

The whole design depends on `TimeWeightedRetriever` treating an absent timestamp as neutral. That behaviour is currently incidental — nothing pins it, so a future change could remove it and re-break this silently.

**Step 1:** Add a test asserting a chunk with **no** `created_at` and **no** fallback key scores exactly its base score — decay `1.0`, no boost, no penalty.

**Step 2:** Prove it can fail — make `ComputeDecay` return something other than 1.0 for a null timestamp, confirm red, revert. **Report the mutation and that it went red.**

---

## Task 6: Documentation

**Files:**
- `docs/guide/retrieval.md` (the time-weighting section)
- `docs/guide/data-providers.md`

State plainly: provider-ingested documents have **no** creation time unless the connector supplies one; time-weighted retrieval is neutral for them rather than wrong; and the fallback keys make seven connectors work today.

**Note:** `docs/guide/data-providers.md` has **five documented members that do not exist in the code** — `ChannelIds`→`ChannelId`, `EmailAddress`/`ImapHost`/`ImapPort`→`UserName`, `SpaceKeys`→`SpaceKey`, `Branch`→`Ref` (GitLab, Bitbucket), found by Phase 4.7's README guard and routed to 4.5. **You are editing that file — fix them while you are there, and say you did.** Leaving known-false documentation in a file you are already touching is how it survives another phase.

---

## Task 7: Close the phase

Update `docs/planning/ROADMAP.md` and `docs/planning/MILESTONE.md` as **Phase 4.9**, matching how 4.7 and 4.8 closed.

Record:

- **The roadmap's own estimate was wrong**, and the evidence was already in the repository: the design doc the entry cited says the one-line fix cannot work. Remove or correct that "slot, not a phase" routing on the 4.2 entry.
- The measured reality: **17 of 25 providers hold a timestamp and discard it**; 4 more do not fetch it. **Schedule that as its own phase** with the DTO/cassette cost priced in — do not leave it as a slot.
- The breaking change: `DocumentMetadata.CreatedAt` is now `DateTime?`.
- What this phase does **not** fix: those 17 connectors now rank **neutrally rather than wrongly**.
- Any correction Task 4 forced to the connector-tag list.

**Do not tick a DoD box this phase did not make true.**

---

## Final verification

```bash
dotnet build Rag.NET.slnx -c Release      # 0 warnings
dotnet test tests/Rag.NET.Tests
dotnet test tests/Rag.NET.DataProviders.Tests
dotnet test tests/Rag.NET.RepoConventions.Tests
dotnet test tests/Rag.NET.PackageValidation.Tests
```

The deliverable is Task 1's test passing for the right reason, with every changed assertion explained.
