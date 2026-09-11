# The Boundary Rag.NET Does Not Watch — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** document the `IChatClient` composition that covers the model boundary — including the registration order it requires — taking on no code and no dependency.

**Architecture:** One section in `docs/guide/security.md`, after the posture and before the feature sections. No `src/` change, no package reference. The only executable work is verifying the registration-order claim, which is testable with repo-local tools and must not be asserted from reading.

**Tech Stack:** Markdown. A throwaway console project for the ordering check.

**Spec:** `docs/plans/2026-09-11-model-boundary-monitoring-design.md` — read it first. **And read §0 below: the design is incomplete in a way that would have produced advice a reader could follow into a loud failure.**

## Global Constraints

- **Phase:** 6.2.39. No issue number.
- **Conventional commits, header at most 100 characters.**
- **NOTHING ENTERS `src/`.** No package reference, no integration code, no abstraction. Task 4 checks this with `git diff`, the way 6.2.38 did — asserting it is not enough.
- **Do not add AI.Sentinel as a dependency of anything in this repository**, including test projects. The verification in Task 2 deliberately uses a trivial local decorator instead, because the claim being verified is about *Rag.NET's* ordering guard, not about AI.Sentinel.
- **Every claim about AI.Sentinel carries its measurement date and the versions measured.** It is a third-party package on its own release schedule; an undated "works fine" is a claim that expires silently.
- **State the overlap and the duplicated cost.** A section that reads as purely additive would be selling. The design's §2 disclosure exists to avoid exactly that, and understating the overlap would undo it.
- **Test command:** `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release` (the docs guards from 6.2.38) and `npm run build` (link validation). Baseline: **101 passed / 2 pre-existing skips** — confirm in Task 0.

---

## §0. The design is missing the thing most likely to bite a reader

The design describes the composition as "decorate the `IChatClient` before Rag.NET's DI consumes it"
and treats that as a one-line matter. **It is not, and the repository already knows why.**

`src/Rag.NET/DependencyInjection/CompositionClaims.cs` exists because of issue #195:

> The extensions that wrap `IChatClient`, `IEmbeddingGenerator` and `IVectorStore` rewrite
> descriptors, so they can only wrap what is registered when they run. A surface registered
> *afterwards* — or a later registration that replaces the decorated one — leaves the feature
> silently absent: no retries, no budget, no failover (issue #195). […] the honest remedy is to fail
> loudly instead.

So a reader who writes the natural thing —

```csharp
services.AddRagNet(b => b.UseCostBudgeting());        // claims IChatClient
services.AddChatClient(inner).UseAISentinel();        // registers it afterwards
```

— gets an exception at resolve time, because Rag.NET's cost-budgeting decorator wrapped nothing. That
is the guard working, and it is a good outcome compared to the silent version. But **documentation
that leads a reader into it without warning is documentation that wasted their afternoon.**

The section must therefore state the required order — **register the monitored `IChatClient` first,
then `AddRagNet`** — and say what happens if you get it backwards, because the failure is loud and
its message is about cost budgeting rather than about ordering.

**This was found by reading `CompositionClaims` while looking for how Rag.NET consumes `IChatClient`,
not by the design.** Task 2 verifies it rather than trusting this reasoning.

---

### Task 0: Baseline

- [x] **Step 1: Record the baseline**

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
```

Expected 101 passed / 2 pre-existing skips (6.2.38 took it from 98). Confirm rather than trust.

- [x] **Step 2: Confirm the docs site builds before touching it**

```bash
npm run build
```

So a failure later in the phase is attributable to this phase's edit rather than inherited.

---

### Task 1: Verify the ordering claim before writing it down

**Files:** nothing in the repository — a throwaway project under the scratchpad.

§0 is reasoning from a source comment. The section will tell readers to order their registrations a
particular way, so the claim gets executed first. **If it turns out wrong, the section changes;
the design does not get to win on the strength of a well-written paragraph.**

- [x] **Step 1: Build the probe**

A `net10.0` console project in the scratchpad referencing the **local** `src/Rag.NET/Rag.NET.csproj`
by `ProjectReference` — not a package, and not AI.Sentinel. A trivial local decorator stands in for
the monitor:

```csharp
internal sealed class NoOpMonitorChatClient(IChatClient inner) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        inner.GetResponseAsync(messages, options, ct);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        inner.GetStreamingResponseAsync(messages, options, ct);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
```

**Read `IChatClient` before writing this** rather than trusting the snippet — the member list has
already caught out one plan in this milestone (`DeleteByDocumentIdAsync` taking a `string`).

- [x] **Step 2: Run both orders and record what actually happens**

Two containers, one assertion each:

- **Correct order** — register the decorated `IChatClient`, then `AddRagNet(b => b.UseCostBudgeting())`,
  then resolve the pipeline. Expected: resolves.
- **Wrong order** — `AddRagNet(b => b.UseCostBudgeting())` first, then register the `IChatClient`,
  then resolve. Expected: throws, **and the message names cost budgeting rather than ordering**.

**Capture the actual exception type and message text.** The section quotes it, because a reader who
hits it will search for the message they saw, not for the concept.

**`UseCostBudgeting` is a guess at which extension claims `IChatClient`.** Confirm which `Use*`
extensions actually call `CompositionClaims` before picking one — grep for the claim, do not assume.
If none of them is convenient, any claiming extension will do; the point is the guard, not the
feature.

**MEASURED 2026-09-11. §0's mechanism is confirmed; §0's characterisation of the message is wrong,
and in the project's favour.**

| Order | `IChatClient` resolves to | Pipeline resolves? |
| --- | --- | --- |
| Monitor registered, **then** `AddRagNet(b => b.UseCostBudgeting(...))` | `CostTrackingChatClient` → wrapping `NoOpMonitorChatClient` → wrapping the provider | **yes** |
| `AddRagNet(...)` first, monitor registered **afterwards** | `NoOpMonitorChatClient` — Rag.NET's decorator silently absent | **throws** |

The thrown message, verbatim:

> UseCostBudgeting is not applied to the IChatClient this container resolves. It decorates whatever
> is registered at the moment it runs, and this IChatClient was registered (or replaced) afterwards,
> so the feature UseCostBudgeting configures is silently absent. Move the UseCostBudgeting call
> after the IChatClient registration. This is checked when the RAG pipeline is resolved because a
> registration made later cannot be seen at registration time.

**§0 predicted the message would name cost budgeting "rather than about ordering", costing the
reader an afternoon. That is false.** It names the ordering, explains the mechanism, and states the
fix in an imperative sentence. The guard is better than the plan gave it credit for, and the section
should not imply a reader would be stranded by it.

**Two findings that survive, and are the reason this task was worth running:**

1. **The guard fires only when the *pipeline* is resolved, not when `IChatClient` is.** Case B
   resolved `IChatClient` perfectly happily to the bare monitor — it was `GetRequiredService<IRagPipeline>()`
   that threw. This is documented behaviour ("checked when the RAG pipeline is resolved"), and it
   matters to anyone who resolves the chat client in a test or a smoke check and concludes the
   composition is fine.
2. **In the correct order, Rag.NET's own decorators wrap *around* the monitor** —
   `CostTrackingChatClient` → `NoOpMonitorChatClient` → provider. That is the right nesting and worth
   stating: the monitor sits closest to the model, so it sees the prompt exactly as sent and the
   response exactly as returned, before any Rag.NET decorator processes it.

**An earlier run of this probe was itself wrong and is recorded rather than deleted**: it resolved
`IChatClient` instead of the pipeline and reported "no guard fired", which would have become a
documented claim that the guard has a hole. The guard's own doc comment says it is checked at
pipeline resolution; the probe had not been reading it.

- [x] **Step 3: Record the result in this file**

Write down what happened, including if it contradicts §0. **A wrong prediction is a finding, not
something to quietly reword around.**

---

### Task 2: Write the section

**Files:** Modify `docs/guide/security.md` — insert after the `### Dependency advisories` subsection
that ends the posture, before `## RBAC on Chunks`.

- [x] **Step 1: Re-read the posture section**

The new section sits directly under it and must not repeat it. The posture already lists the four
feature families and states the boundary; this section is about what sits *outside* that boundary.

- [x] **Step 2: Write it**

`## Watching the model boundary`, covering:

**(a) The gap, concretely.** All four of Rag.NET's security points act before the model is called.
Nothing acts after. `IConfidenceScorer` is not that — it scores whether a sentence is supported by
the retrieved context and fails open at `1.0`, which is a groundedness signal. **Give the concrete
failure**: a secret that survives ingest-time redaction can be summarised back to a user, and no part
of this library looks at that.

**(b) The shape of the answer.** Any `IChatClient` decorator sees both directions. This is a pattern
statement, before any product is named.

**(c) The worked example, naming AI.Sentinel, with the authorship disclosure.** One sentence, plain:
that it is written by the same author as Rag.NET, so the reader can weigh the recommendation
accordingly. Do not bury it in a footnote — the point of disclosing is that it is seen.

**(d) The registration order, per §0 and Task 1.** Register the monitored `IChatClient` **first**,
then `AddRagNet`. Quote the exception from Task 1 Step 2, and say plainly that the message names a
Rag.NET feature rather than the ordering, so a reader who hits it recognises it.

**(e) The overlap, including the cost.** Both do prompt-injection detection by different means —
Rag.NET's sanitisers and guards before the call, a monitor's detectors at the boundary. Defence in
depth **or duplicated cost**, depending on configuration. Say both halves.

**(f) The version caveat, dated and version-named.** AI.Sentinel 2.0.1 targets `net8.0`/`net9.0`
against Rag.NET's `net10.0`, and builds against `ZeroAlloc.Mediator` 4.1.4 and `ValueObjects` 1.7.1
where this repository pins 5.0.1 and 2.0.5. Verified on **2026-09-11** to load and run — 55 detectors
resolving and constructing, a scan completing without `MissingMethodException`. **State what that
does not cover**: construction and the scan path, not every detector's internals.

**(g) Verify detection against your own configuration.** A bare configuration scanned a blatant
injection clean in the 2026-09-11 spike, with the injection-shaped detectors registered — almost
certainly a missing embedding generator, which AI.Sentinel's own quick start sets. The advice is
general and worth the sentence regardless of the cause: **registration is not protection, and the two
look identical from outside.**

- [x] **Step 3: Check the links and the anchors**

```bash
grep -oE "\]\([^)]+\)" docs/guide/security.md | sort -u
```

Read the list. Every relative path must exist and every anchor must match a real heading.

- [x] **Step 4: Build the docs site**

```bash
npm run build
```

Docusaurus validates internal links at build, so this is the link check with teeth.

- [x] **Step 5: Commit**

```bash
git add docs/guide/security.md
git commit -m "docs(security): document the model boundary, and the order the composition requires"
```

---

### Task 3: Run the docs guards

**Files:** none.

6.2.38's `SecurityDocumentationTests` guards this page's package list and its RBAC quote. A new
section should disturb neither, and if it does, that is worth knowing rather than working around.

- [x] **Step 1: Run them**

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
```

Expected: Task 0's baseline, unchanged at 101.

**THIS TASK'S TEST SET WAS WRONG, AND CI CAUGHT WHAT IT MISSED.** The task reasoned that a
markdown-only change affects no other suite, and ran `RepoConventions` plus `npm run build`.
`pack-validate` failed on the PR.

`DocsCodeExamplesTests.EveryDocsCodeExampleResolvesAgainstTheProducedPackages` requires **every C#
example on a published docs page to resolve against what the produced packages actually ship** —
their assemblies, their transitive closures, the shared framework. The worked example referenced
`AddAISentinel`, `UseAISentinel`, `SentinelAction` and `OpenAIChatClient`, none of which Rag.NET
ships or depends on. The guard's own message names the remedy: a reference that is correct but
structurally unresolvable "belongs in `AllowedExternalReferences`, not a docs rewrite".

**The reasoning error is the interesting part.** "No `src/` change" is not the same as "no suite
affected" — this repository validates its *documentation* against its *packages*, so a docs-only
change is exactly the kind that can break packaging validation. The repository's own note that
`dotnet build` cannot reach the `pack-validate` guards was on file and not applied here.

**Two fixes, in this order:**

1. **Trim the example before widening the allowlist.** The guard's class doc warns that growth past
   a handful of entries "is a signal the resolvable set needs widening again, not that the list needs
   to be longer". The severity mapping (`OnCritical`/`OnHigh`, hence both `SentinelAction` failures)
   is AI.Sentinel's configuration detail, not Rag.NET's ordering lesson, so it came out. Five
   failures became three.
2. **Allowlist the remaining three** with reasons, following the `OpenAIChatClient` precedent already
   present for `resilience.md`. The AI.Sentinel entries carry the argument that makes them correct
   rather than tolerated: *a produced package resolving `AddAISentinel` would mean the boundary had
   been crossed, which is exactly what the section says is not done.* The allowlist entry and the
   documentation now assert the same fact.

**Verified locally after the fix, with a clean repack first** (`Remove-Item artifacts/packages`, then
`dotnet pack` from PowerShell — the version guard embeds the branch name): `PackageValidation`
**23/23**, including `EveryAllowedExternalReferenceStillFails`, which re-derives the raw failure set
and rejects an entry that no longer matches. **No new tests in this phase** — the section makes no
mechanical claim a test could pin, and adding one that merely asserts a heading exists would be
ceremony. Say so in the ROADMAP rather than inventing a guard to look thorough.

---

### Task 4: Roadmap, review and PR

- [x] **Step 1: The no-code check, mechanically**

```bash
git diff main...HEAD --name-only | grep "^src/" && echo "CONSTRAINT VIOLATED" || echo "clean"
```

- [x] **Step 2: `docs/planning/ROADMAP.md`**, the Phase 6.2.39 block — record what the phase found,
  in its neighbours' style: §0's ordering trap and whether Task 1 confirmed it. **Do not change the
  `[status: ...]` marker** — `complete-phase` does that after the merge.

- [x] **Step 3: Correct the design** with a struck-through note if Task 1 confirmed §0 — the design
  described the composition as a one-liner and it is not. Same treatment 6.2.36 and 6.2.38 gave their
  designs.

- [x] **Step 4: Report the spike observations to AI.Sentinel's author.** The bare-config clean scan
  and the double-registration via `AddAISentinel` + `AddAISentinelDetectors` (110 instances of 55
  types). **An issue on that repository, not this one** — and the operator is that author, so
  confirm they want it filed rather than assuming.

- [x] **Step 5: Run `pre-push-review`.** Record the verdict and report path.

- [x] **Step 6: Open the PR.** Note that nothing entered `src/`, and record the number here.

---

## Self-review

**Spec coverage** — design §5's three in-scope items: (1) the section → Task 2. (2) the worked example
with disclosure → Task 2(c). (3) overlap, version caveat, verify-your-config → Task 2(e)(f)(g). The
out-of-scope items are enforced by Global Constraints and checked by Task 4 Step 1.

**Beyond the spec** — §0's registration-order material, which the design missed entirely. It is not
scope creep: without it the section's central instruction is incomplete in a way that leads a reader
into an exception whose message points somewhere else.

**Placeholder scan** — Task 1 Step 2 names `UseCostBudgeting` as a guess and says to confirm which
extensions actually claim `IChatClient`. That is an instruction to check, not a gap.

**Known weakness** — the version caveat cannot be tested and will go stale on AI.Sentinel's schedule,
not this repository's. Dating it and naming versions is the whole mitigation; there is no guard to
add, and pretending otherwise would be worse than the staleness.
