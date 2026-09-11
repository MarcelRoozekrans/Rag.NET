# The Feature Family Nobody Documented — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** document the prompt-injection defences in the guide, in the feature reference, and in IntelliSense — without changing any behaviour.

**Architecture:** One new section in `docs/guide/security.md`; a rewrite of one `features.md` entry; a correction to the posture's family table; and `<summary>` comments on ten public `Use*` methods. The only `src/` change is comment lines.

**Tech Stack:** Markdown, C# XML documentation comments.

**Spec:** `docs/plans/2026-09-11-prompt-injection-documentation-design.md` — read it first, **and read §0 below: reading the implementations turned up a second fail-open default the posture does not mention**, which changes what the section leads with.

## Global Constraints

- **Issue:** #552. **Phase:** 6.2.40.
- **Conventional commits, header at most 100 characters.**
- **NO BEHAVIOUR CHANGES.** The `src/` diff must contain only `///` lines. Task 5 checks this by diff, not by assertion — a diff touching an executable line in `RagBuilderExtensions.cs` means the phase exceeded its remit.
- **Describe what the code does, not what the method is called.** `RegexRetrievalGuard`, `TrustLevelRetrievalGuard`, `RegexQuerySanitiser` and `QuerySanitiserPipelineDecorator` carry **zero** doc comments, so every claim about them comes from reading their bodies. This is the mitigation 6.2.38 established and it has already paid out once here — see §0.
- **`pack-validate` runs locally before the PR**, after a clean repack. 6.2.39 shipped a PR that failed `DocsCodeExamplesTests` on the reasoning that markdown cannot break packaging validation. The new section's examples are Rag.NET's own types and *should* resolve, which is exactly the assumption to test rather than trust.
- **Do not widen `DocumentationQualityTests`.** Design §4 rejected it: scanning past `Rag.NET.Abstractions` would surface a long tail across packages and turn this into an unbounded cleanup.
- **Test commands:** `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`, `dotnet test tests/Rag.NET.PackageValidation.Tests -c Release` (after repack), `npm run build`, and a whole-solution build at **0 warnings**.

---

## §0. A second fail-open default, which the posture does not mention

Reading `TrustLevelRetrievalGuard` rather than its name:

```csharp
var trustLevel = result.Chunk.Metadata.TryGetValue(ReservedMetadataKeys.TrustLevel, out var tl)
    ? tl.ToString()
    : "internal";
```

**A chunk with no `trust_level` metadata is treated as `internal`** — the most trusted value — so
`UseTrustLevelGuard` drops nothing for a corpus that was ingested without trust tagging. Registering
it on existing content changes nothing at all, silently.

That is the same shape as RBAC's world-readable default, which 6.2.38's posture calls out as "the
single most important default on this page" and which the RBAC section documents. **The posture says
nothing about trust levels** (`grep -c "trust" docs/guide/security.md` → 0), and the reason is simply
that the family was undocumented when the posture was written.

**Consequence for this plan:** the guide section must state it as prominently as the RBAC section
states its own, and the posture's "RBAC fails open" subsection becomes a "defaults that fail open"
subsection covering both. Documenting the second one and leaving the posture claiming there is one
notable fail-open default would be a new inaccuracy introduced by the phase fixing an inaccuracy.

**Not a proposal to change the default.** Same rule as 6.2.38: the phase writes down what is true. A
guard that hid every untagged chunk the moment it was registered would be the worse failure, exactly
as with RBAC.

---

## §0.2 Query sanitisation does not apply to `RetrieveAsync`

`QuerySanitiserPipelineDecorator` sanitises `AskAsync` and `AskStreamingAsync`. It forwards
`RetrieveAsync` **unchanged**:

```csharp
public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
    string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    => inner.RetrieveAsync(query, options, cancellationToken);
```

So a caller who uses Rag.NET for retrieval only — a common pattern, retrieve chunks and generate
elsewhere — registers `UseQuerySanitiser` and gets nothing on that path.

**There is a defensible reason**: injection is about hijacking a model, `RetrieveAsync` reaches none,
and redacting `act as` from a legitimate query would degrade retrieval for no security gain. **But
nothing records that reasoning.** The file has zero doc comments,
`QuerySanitiserPipelineDecoratorTests` covers `AskAsync`, `AskStreamingAsync`, `IngestAsync`
pass-through and the no-sanitiser case — **and not `RetrieveAsync`** — and no published page mentions
the method at all.

**Consequences for this phase:**

1. The section documents it plainly. Describing `UseQuerySanitiser` without it would manufacture the
   false sense of protection this milestone exists to remove.
2. Task 4's `<summary>` for `UseQuerySanitiser` says it too — the IDE is where most callers meet it.
3. **File the question of intent** (Task 6). Not fix: if the omission is deliberate it needs a comment
   and a test, and if it is not it needs a behaviour change — and a behaviour change does not belong
   in a PR reviewed as documentation. Same rule 6.2.38 and 6.2.39 followed.

---

### Task 0: Baseline

- [ ] **Step 1: Record the baselines**

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release     # expect 101 / 2 skipped
dotnet build Rag.NET.slnx -c Release                            # expect 0 warnings
npm run build                                                    # expect SUCCESS
```

Write the numbers here. The 0-warning build is load-bearing: Task 4 adds XML comments to a file that
currently has none on its public surface, and a malformed `<summary>` or a broken `<see cref="">`
becomes a build warning.

---

### Task 1: The guide section

**Files:** Modify `docs/guide/security.md` — insert after `## Watching the model boundary` and before
`## RBAC on Chunks`.

**Placement matters and is not arbitrary.** The model-boundary section (6.2.39) is about what sits
*outside* the library; this is a feature family *inside* it, so it belongs with its three siblings,
above them in pipeline order.

- [ ] **Step 1: Read every implementation before writing about it**

`RegexChunkSanitiser`, `LlmChunkSanitiser`, `RegexQuerySanitiser`, `LlmQuerySanitiser`,
`RegexRetrievalGuard`, `TrustLevelRetrievalGuard`, `QuerySanitiserPipelineDecorator`,
`PromptHardeningAnswerEngineDecorator`, `InjectionPatterns`, `TrustLevelGuardOptions`,
`PromptHardeningOptions`. Four of those have no doc comments at all.

**Already established by this plan's own reading, and not to be re-derived from names:**

- `InjectionPatterns.InjectionPattern()` is **one shared `[GeneratedRegex]`**, used by the chunk
  sanitiser, the query sanitiser and the regex retrieval guard. Case-insensitive, **1000 ms match
  timeout**. It targets role-switch phrases (`ignore previous instructions`, `you are now`, `act as`,
  `disregard`, `new instructions`, `system prompt`) and delimiter injection (`<|system|>`,
  `<|user|>`, `[INST]`, `### instruction`).
- `RegexQuerySanitiser` replaces matches with `[REDACTED]`, logs a warning naming the matched
  pattern and a 100-character query preview, and **fails open**: any exception returns the original
  query unchanged.
- `RegexRetrievalGuard` redacts within chunk text and tags the activity
  `security.guard.action = redact`; `TrustLevelRetrievalGuard` drops and tags `action = drop`. Both
  emit a `ragnet.security.guard` activity — **worth documenting, because it is how a reader verifies
  the guard ran at all.**
- `TrustLevelGuardOptions` is `DropUntrusted = true`, `WarnOnExternal = true`.

- [ ] **Step 2: Write `## Prompt injection defences`**

Cover, in this order:

**(a) Why, in two sentences**, quoting the reference's own framing: indirect prompt injection is the
primary RAG security risk, where attacker-controlled content carries instructions that hijack the
model at query time.

**(b) The four layers as a table, in pipeline order** — the family is only comprehensible
positionally:

| Layer | Interface | Registration | Runs |
|---|---|---|---|
| Chunk sanitisation | `IChunkSanitiser` | `UseChunkSanitiser` / `UseLlmChunkSanitiser` | ingest, before embedding |
| Query sanitisation | `IQuerySanitiser` | `UseQuerySanitiser` / `UseLlmQuerySanitiser` | before retrieval |
| Retrieval guards | `IRetrievalGuard` | `UseRetrievalGuard` / `UseTrustLevelGuard` | on retrieved chunks |
| Prompt hardening | answer-engine decorator | `UsePromptHardening` | answer assembly |

**(c) The two shared extension points**, which design §5 identifies as the most useful thing the
section can say and which appear in neither existing section:

- **`UseRbac` registers an `IRetrievalGuard`** (`RbacRetrievalGuard`). RBAC and the retrieval guards
  are the same extension point, so they compose by registration order like any other chain.
- **`IChunkSanitiser` is shared with PII redaction.** The same interface, the same ordered chain —
  `UsePiiDetection` and `UseChunkSanitiser` both register into it. Link to the PII section rather
  than restating its chaining rules.

**(d) The regex/LLM pairing**, which recurs three times: a cheap deterministic pass and an expensive
semantic one, registerable independently or chained. The PII section already establishes this shape;
point at it.

**(e) `UseTrustLevelGuard` fails open**, per §0, with the same prominence the RBAC section gives its
own default: **a chunk with no `trust_level` metadata is treated as `internal`**, so registering the
guard over an untagged corpus drops nothing. Say where the level is meant to be set — at ingest, by
whatever pulls from an adversarial source — and link the ingestion guide rather than duplicating it.

**(f) How to tell a guard ran.** The `ragnet.security.guard` activity carries
`security.guard.type` and `security.guard.action`. This is the section's answer to the
"registration is not protection" advice the model-boundary section gives, made concrete.

**(g) A worked registration example** showing the layers composed. **Every type and method in it must
be one the produced packages ship** — this is checked, see Task 5.

- [ ] **Step 3: Build the docs site** — `npm run build`, which validates internal links.

- [ ] **Step 4: Commit**

```bash
git add docs/guide/security.md
git commit -m "docs(security): document the prompt-injection defences (#552)"
```

---

### Task 2: Correct the posture

**Files:** Modify `docs/guide/security.md` — the posture's family table and its fail-open subsection.

- [ ] **Step 1: Repoint the family table**

The prompt-injection row currently reads **"not on this page"** and links `features.md`. It now links
the Task 1 section. **Delete "Until that section is written here, the reference is the place to
read"** — design §2 records why that sentence has to go: it sends a reader to a proposal.

Also remove the `#552` link from that row; the issue is closed by this phase.

- [ ] **Step 2: Widen the fail-open subsection**

`### RBAC fails open` becomes a subsection covering **both** defaults — RBAC's world-readable chunks
and trust level's `internal` fallback (§0). Keep the RBAC sentence quoted verbatim: 6.2.38's
`ThePostureQuotesTheRbacDefaultVerbatimFromTheSectionBelowIt` asserts it appears at least twice in
the file, and rewording the quote breaks that guard.

**Run that guard immediately after this step** rather than at the end — it is the one most likely to
be tripped by this task, and finding out in Task 6 wastes the intervening work.

- [ ] **Step 3: Commit**

```bash
git add docs/guide/security.md
git commit -m "docs(security): point the posture at the section that now exists"
```

---

### Task 3: Rewrite the `features.md` entry

**Files:** Modify `docs/reference/features.md` — the "Prompt Injection Fortification" section only.

- [ ] **Step 1: Replace the proposal with a description of what shipped**

The current body is future tense about work to be undertaken. Replace it with: what the package
actually provides (the four layers, named), a link to the new guide section for detail, and the
`Rag.NET.Parsers.Vision` note **only if it is still true** — check whether that internal
`PromptInjectionSanitiser` still exists before repeating the claim.

**Keep the `**Status:** ✅ Done` line and the `**Package:**` line.** `FeatureClaimTests` parses those,
and the status is correct — it was the body that lied.

- [ ] **Step 2: Run the feature-claim guard**

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release --filter "FullyQualifiedName~FeatureClaim"
```

- [ ] **Step 3: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs(reference): describe the prompt-injection feature that shipped, not the plan"
```

---

### Task 4: XML documentation on the ten registration methods

**Files:** Modify `src/Rag.NET.Security/RagBuilderExtensions.cs`.

**The only `src/` change in this phase, and it must be comment lines only.**

- [ ] **Step 1: Write a `<summary>` for each**

All ten: `UseChunkSanitiser`, `UseLlmChunkSanitiser`, `UseQuerySanitiser`, `UseLlmQuerySanitiser`,
`UseRetrievalGuard`, `UseTrustLevelGuard`, `UsePromptHardening`, `UseRbac`, `UsePiiDetection`,
`UseLlmPiiDetection`.

**Say what the member does, means, or is for — never restate its name.** `DocumentationQualityTests`
enforces exactly that rule for `Rag.NET.Abstractions` and does not reach this file; the rule is right
regardless of whether a guard is watching. "Uses the chunk sanitiser" is the failure mode.

Each summary should carry the fact a caller cannot infer from the signature. For example: that
`UseTrustLevelGuard` treats untagged chunks as `internal`; that `UseQuerySanitiser` and
`UsePiiDetection` register into chains shared with other extensions; that the LLM variants require an
`IChatClient`.

**Where a method has a non-obvious dependency, document it**: `UseLlmChunkSanitiser`,
`UseLlmQuerySanitiser` and `UseLlmPiiDetection` call `GetRequiredService<IChatClient>()`, so they
throw at resolution if none is registered.

- [ ] **Step 2: Build and confirm no new warnings**

```bash
dotnet build Rag.NET.slnx -c Release
```

Expected: **0 warnings**, matching Task 0. A malformed `<see cref="">` surfaces here.

- [ ] **Step 3: Confirm the diff is comments only**

```bash
git diff --stat src/
git diff src/ | grep -E "^[+-]" | grep -vE "^(\+\+\+|---)" | grep -v "^[+-]\s*///" || echo "comments only — constraint holds"
```

**If that command prints anything, an executable line changed.** Stop and revert it.

- [ ] **Step 4: Commit**

```bash
git add src/Rag.NET.Security/RagBuilderExtensions.cs
git commit -m "docs(security): document the registration surface for IntelliSense readers"
```

---

### Task 5: The packaging guard, run locally this time

**Files:** none.

This is the step 6.2.39 skipped on the reasoning that markdown cannot break packaging validation.

- [ ] **Step 1: Clean repack**

```powershell
Remove-Item -Recurse -Force artifacts/packages
$v = dotnet dotnet-gitversion /output json /showvariable SemVer
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$v"
```

**Clear the directory first** — the version guard embeds the branch name, and packing over a previous
branch's output leaves both generations present, turning one failure into three. From PowerShell,
because Git Bash cannot locate `.git` for GitVersion.

- [ ] **Step 2: Run it**

```bash
dotnet test tests/Rag.NET.PackageValidation.Tests -c Release
```

Expected **23/23**. `DocsCodeExamplesTests` checks every C# example in the new section against what
the produced packages ship. These are Rag.NET's own types, so a failure here means the example names
something that does not exist — **fix the example, not the allowlist.** The allowlist is for
references that are correct but structurally unresolvable; a wrong Rag.NET API is neither.

---

### Task 6: Roadmap, issue, review and PR

- [ ] **Step 1: Run every affected suite.** Enumerate, do not reason about which are safe to skip —
  that reasoning failed in 6.2.39:

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
dotnet test tests/Rag.NET.PackageValidation.Tests -c Release
dotnet build Rag.NET.slnx -c Release
npm run build
```

~~`Rag.NET.Security` has no test project of its own; its behaviour is unchanged and the comment-only
diff from Task 4 Step 3 is the evidence.~~

**WRONG, AND WRONG IN THIS PLAN'S OWN NAMED FAILURE MODE.** `tests/Rag.NET.Security.Tests` exists
with 16 test files. This sentence reasoned about which suites could not be affected instead of
enumerating them — the exact thing the Global Constraints and Task 6 Step 1 tell the implementer not
to do, written two paragraphs above it. Add to the list:

```bash
dotnet test tests/Rag.NET.Security.Tests -c Release
```

- [ ] **Step 2: File the `features.md` audit question** — are other ✅ Done entries stale proposals?
  53 entries, and `FeatureClaimTests` only checks that named packages exist. Design §4 puts this out
  of scope; filing it is how it stays recorded rather than lost.

- [ ] **Step 3: Comment on #552** with what shipped and, specifically, that the issue understated the
  problem — six methods documented nowhere, not "detail lives in the reference". Let the PR close it.

- [ ] **Step 4: `docs/planning/ROADMAP.md`**, the Phase 6.2.40 block — record what the phase found,
  including §0's second fail-open default. **Do not change the `[status: ...]` marker.**

- [ ] **Step 5: Amend the design** with a struck-through note for §0 — the design did not anticipate
  the trust-level default and its §5 outline did not include it. Same treatment the last three phases
  gave their designs.

- [ ] **Step 6: Run `pre-push-review`.** Record the verdict and report path.

- [ ] **Step 7: Open the PR.** Note the comment-only `src/` change and how it was verified. Record the
  number here.

---

## Self-review

**Spec coverage** — design §4's four in-scope items: (1) the guide section → Task 1. (2) the
`features.md` rewrite → Task 3. (3) the posture correction → Task 2. (4) XML docs on all ten → Task 4.
Out-of-scope items are enforced by Global Constraints; the audit question is filed in Task 6 Step 2
rather than dropped.

**Beyond the spec** — §0's trust-level fail-open default, which the design did not anticipate. It is
not scope creep: documenting one fail-open default while the posture claims there is a single notable
one would introduce a fresh inaccuracy into the page this phase exists to make accurate.

**Ordering risk, called out** — Task 2 edits the same file as Task 1 and touches text a 6.2.38 guard
asserts. Task 2 Step 2 runs that guard immediately rather than deferring to Task 6, because
discovering it in Task 6 wastes Tasks 3 through 5.

**Known weakness** — no test can check the section is *accurate*. The mitigation is reading every
implementation, which is a discipline the plan can require and not enforce; §0 is evidence it works,
having already found something both prior documents missed.
