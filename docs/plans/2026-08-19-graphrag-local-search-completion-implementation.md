# GraphRAG Local Search — Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the specification-faithful local search begun in #316–#321 — retire the
PageRank blend from the default path, add conversation history, and put the result on the
MultiHop-RAG scale against the same control Milestone 5.2 used, so that "GraphRAG does not help
on this corpus" is finally a statement about local search rather than about a blend.

**Architecture:** `IGraphRagSearch` already exists as a separate entry point (#321), with a pure
`LocalSearchContextBuilder` fed by a `LocalSearchInputs` record that the search populates from the
graph store, the graph chunk store and the document store. This plan adds one section to that
builder (conversation history), stops the old `IRetrievalBehavior` blend being registered by
default, and adds one arm to the existing MultiHop-RAG answer harness so the new path gets a
number under the same prompt, model and top-6 discipline as `dense`, `control` and `filtered`.

**Tech Stack:** .NET 10, C#, xunit v3 (**not** v4 — reverted in d696d31b, it breaks every test
project on the .NET 10 SDK), NSubstitute, ZeroAlloc.Validation source generator,
`Microsoft.ML.Tokenizers` with `Cl100kBase`, OpenRouter (`openai/gpt-4o-mini`) for the answer run.

**Spec:** `docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md` — quotes upstream source
rather than paraphrasing it, because a paraphrase is what lost step 3 the first time. Read it
before Task 1. Task 2 extends it.

## Scope — what this plan is and is not

The spec's phase table lists seven sub-phases. Four are already merged (6.x.2 context builder
#317, 6.x.3 relationships #320, 6.x.4 provenance #319, plus the #321 entry point). This plan
covers **6.x.1, 6.x.6 and 6.x.7**.

**6.x.5 (covariates) is deliberately deferred to its own plan**, on two pieces of evidence, not
taste:

1. Upstream defaults covariates **off** (spec §7 and the "Decided, 2026-08-18" note: *"They stay
   off by default, as upstream"*). The 6.x.7 measurement runs at defaults, so covariates cannot
   move its number. Sequencing them first would block the measurement — which the spec calls
   *"the point of all of it"* — behind work that provably does not affect it.
2. Covariates are a different subsystem: a `GraphCovariate` model, a table and schema migration
   in `SqliteGraphStore`, an extraction pass in the ingestion pipeline, and a context section.
   `LocalSearchContextBuilder.BuildLocal`'s own comment records that a covariate section is the
   one thing that would force reproducing upstream's quadratic per-entity rebuild loop — so it
   changes the builder's shape, not just its output.

Phase 6.2.1 in `docs/planning/ROADMAP.md` is broader still (RAPTOR, HyDE, hybrid, reranking,
late chunking, SPLADE, the three answer engines, per-store SciFact parity, the pipeline-parity
test, #176). Each of those is its own plan. This one is the GraphRAG local-search thread only.

## Global Constraints

Exact values, copied from the repository they apply to. Every task's requirements include these.

- **Commit format:** conventional commits, enforced by `.commitlintrc.yml` in CI on PR commits
  only. Types: `bench`, `build`, `chore`, `ci`, `docs`, `feat`, `fix`, `perf`, `refactor`,
  `revert`, `style`, `test`. **No `scope-enum` rule — scopes are free.** Header cap **100
  characters**; body lines uncapped.
- **Branch per task group, PR to `main`.** Never commit feature work directly to `main`; never
  base a PR on another open PR's branch.
- **Token counting is `cl100k_base`** via `ContextTable.CountTokens`, the same encoding
  `ContextBudgetBehavior` and `ConversationMemoryPipeline` use. Do not introduce a second
  tokenizer or a character estimate.
- **Options types are validated by the ZeroAlloc.Validation generator.** A new property on
  `LocalSearchContextOptions` needs its attribute and is checked in
  `LocalSearchContextBuilder`'s constructor, which throws `ArgumentException`.
- **Doc-comment density matches the surrounding files.** Every public member carries `<summary>`;
  every deviation from upstream is marked `<b>Deviation</b>` and nowhere else. This is not
  decoration — it is how the spec stays attached to the code that implements it.
- **`VerifiedBy` ledger:** `Rag.NET.GraphRag.csproj` is `<VerifiedBy>benchmark</VerifiedBy>`.
  Do not lower it. Task 6 updates the comment above it with the new figure.
- **New tests for local search go in `tests/Rag.NET.GraphRag.Tests/LocalSearch/`**, matching
  `LocalSearchContextBuilderTests.cs`, `GraphRagSearchTests.cs`, `SourceChunkSelectionTests.cs`.
- **Build and test commands:** `dotnet build Rag.NET.slnx` and
  `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj`.

---

### Task 1: Retire the PageRank blend from the default path

**Why this deprecates rather than deletes, against the spec's phase table.** The spec's 6.x.1 row
says "Delete the PageRank blend and `CollectTopEntities`". Deleting the blend outright would make
three pinned figures unreproducible: `MultiHopRagAnswerReproduction`'s `local` arm (0.2102,
measured at `PageRankWeight = 0.3`), `BeirReproduction`'s `GraphRag` nDCG (0.56897), and the
ablation in `BeirGraphRagCorpusTests` that measured the blend as the entire −0.02761. This
repository's discipline is that a pinned figure stays reproducible or is retired on the record.

So: `CollectTopEntities` is deleted (it is genuinely dead since #312), the behaviour stops being
placed by `UseGraphRag`, the blend arithmetic stays callable for the benchmark, and the
deprecation is recorded in `<remarks>` on both members — **not** as an `[Obsolete]` attribute, per
the pre-flight ruling in Step 5. Deletion is scheduled for after Task 6 publishes a replacement
figure, recorded as a follow-up debt in ROADMAP.md rather than left as an open note.

**Files:**
- Modify: `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs` — delete `CollectTopEntities`, add `[Obsolete]`
- Modify: `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs:74` — `[Obsolete]` on `PageRankWeight`
- Modify: `src/Rag.NET.GraphRag/RagBuilderExtensions.cs:210` — stop adding it to the retrieval pipeline
- Modify: `src/Rag.NET.GraphRag/README.md` — lines 29, 47, 94
- Modify: `docs/planning/ROADMAP.md` — record the scheduled removal as a debt under Phase 6.2.1
- Test: `tests/Rag.NET.GraphRag.Tests/PipelinePlacementTests.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/RagBuilderExtensionsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks — this is the first.
- Produces: `AddGraphRag` no longer places `GraphLocalSearchBehavior` in the retrieval pipeline.
  `IGraphRagSearch` remains the registered local-search surface. Task 5 constructs
  `GraphLocalSearchBehavior` directly in the benchmark, so it must stay `public` and constructible.

- [ ] **Step 1: Prove `CollectTopEntities` is dead before deleting it**

Run:
```bash
grep -rn "CollectTopEntities" --include=*.cs src tests
```
Expected: exactly one hit — the declaration at `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs:66`.
If any call site exists, STOP: the spec's "dead code since #312" claim is wrong and this task
needs re-planning rather than a deletion.

- [ ] **Step 2: Invert the two placement tests that assert the old default**

`PipelinePlacementTests.cs` already asserts the behaviour it is now this task's job to remove, in
two places. Both change; a third stays and becomes the evidence for the design.

**2a — `UseGraphRag_WithNoPipelineDelegates_PlacesLocalSearchBeforeReranking` (line 75).** Replace
it wholesale, keeping the file's own idiom (`RetrievalChain(services)` returning a `List<Type>`):

```csharp
[Fact]
public void UseGraphRag_WithNoPipelineDelegates_LeavesTheObsoleteLocalSearchOutOfTheChain()
{
    // At PageRankWeight 0 — the default since #296 — the behaviour skips the graph walk and
    // returns its input unchanged, reproducing the candidate-set control on 2,255 of 2,255
    // queries. So unregistering it changes nothing any default user sees; what it stops is a
    // no-op standing where a reader expects local search to be. Local search is IGraphRagSearch,
    // which AddGraphRag registers as a service rather than placing in the retrieval chain.
    var services = new ServiceCollection();
    services.AddRagNet(rag => rag.UseGraphRag());

    Assert.DoesNotContain(typeof(GraphLocalSearchBehavior), RetrievalChain(services));
    Assert.Contains(services, d => d.ServiceType == typeof(IGraphRagSearch));
}
```

The second assertion matters: this is the same shape
`UseGraphRag_WithNoPipelineDelegates_LeavesGlobalSearchOutOfTheChain` (line 101) already uses for
global search — out of the chain, still resolvable — so local search now follows the pattern the
file established rather than a new one.

**2b — `UseGraphRag_WithGlobalSearchPlacedByHand_HonoursTheCallersChoice` (line 113).** Delete its
`Assert.Contains(typeof(GraphLocalSearchBehavior), types);` line. The test is about the caller's
global-search placement being honoured; the local-search assertion was incidental to the old
default and is now false.

**2c — `UseGraphRag_WithTheDocumentedDelegates_StillPlacesEachBehaviourExactlyOnce` (line 132)
does NOT change.** That test places `GraphLocalSearchBehavior` explicitly through the `retrieval`
delegate, so `Assert.Equal(1, retrieval.Count(t => t == typeof(GraphLocalSearchBehavior)))` still
holds — and it is now the test proving the claim in this task's Interfaces block: a caller who
places the behaviour deliberately still gets it. Leave it alone and say so in the commit body.

- [ ] **Step 3: Run them to verify 2a fails and 2c passes**

Run: `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "FullyQualifiedName~PipelinePlacementTests"`
Expected: `LeavesTheObsoleteLocalSearchOutOfTheChain` FAILS — the behaviour is still placed —
while `StillPlacesEachBehaviourExactlyOnce` PASSES both before and after this task. If 2c fails at
this point, the explicit-placement path is broken and that is a different bug; stop and diagnose
before continuing.

- [ ] **Step 4: Delete `CollectTopEntities` and stop registering the behaviour**

In `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs`, delete the whole `CollectTopEntities`
method (declaration at line 66 through its closing brace).

In `src/Rag.NET.GraphRag/RagBuilderExtensions.cs`, remove the
`.Add<GraphLocalSearchBehavior>(before: typeof(RerankingBehavior))` call at line 210. Keep the
`services.AddSingleton<GraphLocalSearchBehavior>(...)` registration at line 170 — the type stays
resolvable for anyone who placed it deliberately, and for the benchmark.

- [ ] **Step 5: Record the deprecation in `<remarks>` — deliberately NOT `[Obsolete]`**

**Controller ruling, made at pre-flight — do not "restore" the attribute.** `Directory.Build.props:16`
sets `TreatWarningsAsErrors=true`, so `CS0618` is a build **error**, and these two members are
referenced by 17 files across four projects — every reference a deliberate use that this task's own
rationale exists to preserve. `PageRankWeight` additionally carries
`[Must(nameof(PageRankWeightIsFinite))]`, whose *generated* validator code references the property;
a `#pragma` in the hand-written file cannot suppress a diagnostic emitted into generator output.

The load-bearing half of spec phase 6.x.1 is that the blend stops running by default, and Step 4
achieves that. So the deprecation is documented, not enforced by the compiler — the scheduled
deletion (Step 8) will give any external caller a hard error carrying the same message.

In `src/Rag.NET.GraphRag/GraphLocalSearchBehavior.cs`, add to the class's `<remarks>`:

```csharp
/// <para>
/// <b>Deprecated, and no longer placed by <c>UseGraphRag</c>.</b> Local search is
/// <see cref="LocalSearch.IGraphRagSearch"/>. This behaviour blends PageRank into dense retrieval
/// scores, which is not in Microsoft's local search at all: that blend was the entire −0.02761
/// nDCG@10 charged to GraphRAG in Milestone 5.2, and at <c>PageRankWeight = 0</c> — the default
/// since #296 — the ranking matched the candidate-set control on 2,255 of 2,255 queries. It is
/// retained rather than deleted so the figures measured through it stay reproducible:
/// <c>MultiHopRagAnswerReproduction</c>'s local arm (0.2102 at weight 0.3) and
/// <c>BeirReproduction</c>'s GraphRag nDCG (0.56897). Scheduled for deletion once Phase 6.x.7
/// publishes the replacement figure.
/// </para>
```

In `src/Rag.NET.GraphRag/GraphRagRetrievalOptions.cs`, add the same `<b>Deprecated</b>` paragraph
to `PageRankWeight`'s `<remarks>` at line 74, in one sentence plus the 2,255-of-2,255 measurement.

**Add no `[Obsolete]`, no `#pragma warning disable`, and no `NoWarn`.** If you find yourself adding
a suppression, the attribute went in against this ruling — take it out instead.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj`
Expected: PASS, including the existing `GraphLocalSearchBehaviorTests` — the arithmetic is
unchanged, only its registration and its dead helper are gone. Because Step 5 adds no attribute,
no consuming file needs a suppression; if you are seeing `CS0618` anywhere, Step 5 was done wrong.

If `RagBuilderExtensionsTests` asserts the old placement, update that assertion the same way as
Step 2 and say so in the commit body.

Then run the wider build, because the members this task touches are referenced from three other
projects (`Rag.NET.Benchmarks.Quality.IntegrationTests`, `Rag.NET.E2ETests`, and core):

Run: `dotnet build Rag.NET.slnx`
Expected: no errors and no new warnings — `TreatWarningsAsErrors=true` is set repo-wide in
`Directory.Build.props:16`, so a warning anywhere is a failure here.

- [ ] **Step 7: Update the package README**

In `src/Rag.NET.GraphRag/README.md`, change lines 29, 47 and 94 so the documented pipeline no
longer places `GraphLocalSearchBehavior`, and the "for entity questions" sentence at line 94
points at `IGraphRagSearch` instead. Show the new usage:

```csharp
var search = provider.GetRequiredService<IGraphRagSearch>();
var answer = await search.LocalSearchAsync("Which analysts covered both companies?");
```

- [ ] **Step 8: Record the scheduled removal in the roadmap**

In `docs/planning/ROADMAP.md`, under Phase 6.2.1's block, append to the debt list:

```markdown
- **`GraphLocalSearchBehavior` and `PageRankWeight` are `[Obsolete]`, not deleted** (2026-08-19,
  Task 1 of the local-search completion plan). Deleting them would make three pinned figures
  unreproducible — the `local` answer arm at 0.2102, `BeirReproduction`'s GraphRag 0.56897, and
  the blend ablation. They are unregistered from the default pipeline, where at the default
  `PageRankWeight = 0` they were a no-op. → **delete once 6.x.7 publishes the replacement
  figure**, in the same phase.
```

- [ ] **Step 9: Verify and commit**

Run: `dotnet build Rag.NET.slnx` and confirm no new warnings.
```bash
git add src/Rag.NET.GraphRag tests/Rag.NET.GraphRag.Tests docs/planning/ROADMAP.md
git commit -m "refactor(graphrag)!: unregister the PageRank blend — local search is IGraphRagSearch"
```
The `!` is load-bearing: `AddGraphRag` no longer places a behaviour it used to place. The body
must state that the behaviour was a no-op at the default `PageRankWeight = 0`, so no default user
sees a ranking change, and name the three pinned figures the retained type keeps reproducible.

---

### Task 2: Record upstream's conversation-history specification

**Why this is a task and not a paragraph in Task 3.** The spec document exists because *"a
paraphrase is what lost step 3"*. Its coverage of conversation history is one clause — built
first, tokens subtracted before the proportions — and one row in the defaults table
(`conversation_history_max_turns = 5`). It does not record the turn model, the rendering, the
banner name, or whether history is folded into the query before entity selection. Implementing
from that clause would be writing the paraphrase this method exists to prevent.

**Files:**
- Modify: `docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md` — new `## 9` section

**Interfaces:**
- Consumes: nothing.
- Produces: the section Task 3 implements against. Task 3's rendering code is invalid without it.

- [ ] **Step 1: Fetch the upstream conversation-history source**

```bash
gh api "repos/microsoft/graphrag/contents/packages/graphrag/graphrag/query/context_builder/conversation_history.py" --jq '.content' | base64 -d > /tmp/conversation_history.py
gh api "repos/microsoft/graphrag/contents/packages/graphrag/graphrag/query/structured_search/local_search/mixed_context.py" --jq '.content' | base64 -d > /tmp/mixed_context.py
```

If `gh` is unauthenticated, run `gh auth login` first. If the paths 404, upstream has moved them:
find the new path with
`gh api "repos/microsoft/graphrag/git/trees/main?recursive=1" --jq '.tree[].path' | grep conversation`
and record the path you actually read in the section below.

- [ ] **Step 2: Read the source and record what it says**

Read `/tmp/conversation_history.py` in full, plus the `conversation_history` handling in
`build_context` in `/tmp/mixed_context.py`. Append to
`docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md`, before `## Open questions`:

```markdown
## 9. Conversation history

Read from `packages/graphrag/graphrag/query/context_builder/conversation_history.py` on
2026-08-19, at commit `<sha from gh api>`. Quoted, not paraphrased, for the reason in the
opening section.

### 9.1 The turn model
<the ConversationRole values, the ConversationTurn fields, and how a history is constructed>

### 9.2 What reaches the context
<build_context's signature: max turns, whether QA pairs are paired, the recency direction>

### 9.3 Rendering
<the exact banner text, the column header, the delimiter, and whether it is a table at all>

### 9.4 The budget interaction
<where the subtraction happens relative to the three proportions, and what happens when history
alone exceeds max_context_tokens>

### 9.5 Entity selection
<whether history text is concatenated into the query before map_query_to_entities, or not>

### 9.6 What this library cannot reproduce
<anything requiring a field Rag.NET's model does not carry — marked as a Deviation for Task 3>
```

Fill every angle-bracketed slot with what the source says. An empty slot means the source was not
read. If upstream's behaviour cannot be reproduced here, say so in 9.6 — that is a finding, not a
failure.

- [ ] **Step 3: Settle open question 1 with the evidence now in hand**

The spec's open question 1 — *"Does the answer prompt change?"* — was to be decided "when 6.x.2
renders its first context". It has: `LocalSearchPrompt` exists and `LocalSearchContextBuilder`
renders the sections. Replace the open question with a decision, recording which prompt the
6.x.7 measurement uses and why. **The recommended answer, for Task 5's benefit: the measurement
uses `BeirGraphRagAnswerTests`' shared `PromptTemplate`, not `LocalSearchPrompt`**, because every
other arm uses it and changing the context and the prompt together would confound the two — the
same isolation argument that made the `filtered` arm interpretable. `LocalSearchPrompt` stays the
library default for `LocalSearchAsync`; measuring it against the others is a separate arm and a
separate question.

- [ ] **Step 4: Commit**

```bash
git add docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md
git commit -m "docs(plans): upstream's conversation history, read from source"
```

---

### Task 3: Conversation history in the context builder

**Files:**
- Create: `src/Rag.NET.GraphRag/LocalSearch/ConversationTurn.cs`
- Modify: `src/Rag.NET.GraphRag/LocalSearch/LocalSearchContextOptions.cs`
- Modify: `src/Rag.NET.GraphRag/LocalSearch/LocalSearchInputs.cs`
- Modify: `src/Rag.NET.GraphRag/LocalSearch/LocalSearchContext.cs`
- Modify: `src/Rag.NET.GraphRag/LocalSearch/LocalSearchContextBuilder.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/LocalSearch/LocalSearchContextBuilderTests.cs`

**Interfaces:**
- Consumes: `## 9` of the spec, from Task 2 — specifically 9.1 for `ConversationTurn`'s shape,
  9.3 for the banner and header strings, 9.4 for where the subtraction lands.
- Produces:
  - `public sealed record ConversationTurn(ConversationRole Role, string Content)`
  - `public enum ConversationRole { User, Assistant, System }`
  - `LocalSearchInputs.ConversationHistory` — `IReadOnlyList<ConversationTurn>`, default `[]`,
    oldest turn first
  - `LocalSearchContextOptions.ConversationHistoryMaxTurns` — `int`, default `5`, counting **QA
    pairs, not messages**
  - `LocalSearchContextOptions.IncludeUserTurnsOnly` — `bool`, default `true`
  - `LocalSearchContextOptions.ConversationHistoryRecencyBias` — `bool`, default `false`
  - `LocalSearchContext.History` — `SectionFill`

  Task 4 populates `ConversationHistory`; Task 5 reads `LocalSearchContext.Text`.

  **Spec §9 was written after this plan and overturned four of its assumptions.** They are already
  corrected in the steps below — the record and enum shape match §9.1, the table is two columns per
  §9.3, the cap counts QA pairs and keeps the oldest per §9.1–9.2, and only user turns render by
  default. §9 is the authority; if a step below still disagrees with it, §9 wins and the step is
  stale — say so in your report rather than splitting the difference.

- [ ] **Step 1: Write the failing test for the budget subtraction**

Add to `tests/Rag.NET.GraphRag.Tests/LocalSearch/LocalSearchContextBuilderTests.cs`:

```csharp
[Fact]
public void HistoryTokensComeOffTheTotalBeforeTheProportions()
{
    // Spec section 2: history is built first and its tokens are subtracted from
    // max_context_tokens BEFORE the three proportions divide what is left. Applying the
    // proportions to the full total and then prepending history overruns the budget by exactly
    // the history's size, which is the failure mode this pins.
    var options = new LocalSearchContextOptions
    {
        MaxContextTokens = 1_000,
        CommunityProportion = 0.15,
        TextUnitProportion = 0.5,
    };

    var withoutHistory = new LocalSearchContextBuilder(options).Build(FullInputs());
    var withHistory = new LocalSearchContextBuilder(options).Build(FullInputs() with
    {
        ConversationHistory =
        [
            new ConversationTurn(ConversationRole.User, string.Join(' ', Enumerable.Repeat("spectroscopy", 100))),
        ],
    });

    Assert.True(withHistory.History.Tokens > 0, "the history section rendered nothing");
    Assert.True(
        withHistory.TokenCount <= options.MaxContextTokens,
        $"context is {withHistory.TokenCount} tokens against a {options.MaxContextTokens} budget");
    Assert.True(
        withHistory.Sources.Budget < withoutHistory.Sources.Budget,
        "the source budget did not shrink, so history was not subtracted before the split");
}
```

`FullInputs()` is this file's existing fixture builder at line 262 — it returns a
`LocalSearchInputs` with entities, relationships, communities and source chunks already populated,
which is what makes the "did the other sections shrink" assertion meaningful. Do not add a second
fixture builder beside it.

- [ ] **Step 2: Write the failing test for assembly order**

```csharp
[Fact]
public void HistoryIsTheFirstSectionInTheContext()
{
    // Spec section 6: [conversation history] + [communities] + [entities, relationships,
    // covariates] + [text units]. Order is not cosmetic — the prompt reads the sections
    // positionally.
    var context = new LocalSearchContextBuilder(new LocalSearchContextOptions()).Build(
        FullInputs() with
        {
            ConversationHistory = [new ConversationTurn(ConversationRole.User, "Who audited it?")],
        });

    var history = context.Text.IndexOf("-----Conversation History-----", StringComparison.Ordinal);
    var reports = context.Text.IndexOf("-----Reports-----", StringComparison.Ordinal);

    Assert.True(history >= 0, "no conversation history section was rendered");
    Assert.True(reports < 0 || history < reports, "history did not come first");
}
```

**Use the banner string spec §9.3 records**, not the one guessed here, if they differ.

- [ ] **Step 3: Write the failing test for the turn cap — it keeps the OLDEST turns**

Spec §9.2, verified at source. `mixed_context.py:165` calls `build_context(..., recency_bias=False)`,
and the truncation is `if recency_bias: qa_turns = qa_turns[::-1]` followed by
`qa_turns = qa_turns[:max_qa_turns]`. With no reversal, taking the first N keeps the **oldest** N
QA turns. Upstream's own docstring says the parameter defaults to `True`; the only call site passes
`False`. Reproduce the call site — it is what runs.

```csharp
[Fact]
public void TheOldestQaTurnsAreKept_BecauseUpstreamDisablesRecencyBias()
{
    // Counter-intuitive and deliberate. See spec section 9.2: recency_bias defaults True in the
    // docstring and is passed False by the only caller, so a long conversation contributes its
    // BEGINNING, not its most recent exchanges. Reproduced rather than corrected, for the reason
    // EntityOversampleScaler records: which turns reach the context changes what the model sees,
    // so "fixing" it silently would make this a different retrieval system that resembled the
    // specification. Set ConversationHistoryRecencyBias = true for the intuitive behaviour.
    var turns = Enumerable.Range(1, 9)
        .Select(i => new ConversationTurn(ConversationRole.User, $"question {i}"))
        .ToList();

    var context = new LocalSearchContextBuilder(
            new LocalSearchContextOptions { ConversationHistoryMaxTurns = 5 })
        .Build(FullInputs() with { ConversationHistory = turns });

    Assert.Equal(5, context.History.Rendered);
    Assert.Contains("question 1", context.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("question 9", context.Text, StringComparison.Ordinal);
}

[Fact]
public void RecencyBiasReversesWhichTurnsSurvive()
{
    var turns = Enumerable.Range(1, 9)
        .Select(i => new ConversationTurn(ConversationRole.User, $"question {i}"))
        .ToList();

    var context = new LocalSearchContextBuilder(
            new LocalSearchContextOptions
            {
                ConversationHistoryMaxTurns = 5,
                ConversationHistoryRecencyBias = true,
            })
        .Build(FullInputs() with { ConversationHistory = turns });

    Assert.Contains("question 9", context.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("question 1", context.Text, StringComparison.Ordinal);
}
```

- [ ] **Step 3b: Write the failing test for QA grouping and user-turns-only**

Spec §9.1 and §9.2. The cap counts **QA turns**, not raw messages: only a `User` turn starts a new
pair, and every non-user turn between two user turns is absorbed into the preceding pair's answers —
including a `System` turn, which then renders under the literal string `assistant`. And
`include_user_turns_only` defaults `True`, so by default **only the user's questions render at all**.

```csharp
[Fact]
public void TheCapCountsQaPairsAndOnlyUserTurnsRenderByDefault()
{
    // Six messages, three QA pairs. Spec section 9.1: only User opens a pair, so the assistant
    // replies below are absorbed into the pair above them rather than counting against the cap.
    var turns = new List<ConversationTurn>
    {
        new(ConversationRole.User, "question 1"),
        new(ConversationRole.Assistant, "answer 1"),
        new(ConversationRole.User, "question 2"),
        new(ConversationRole.Assistant, "answer 2"),
        new(ConversationRole.User, "question 3"),
        new(ConversationRole.Assistant, "answer 3"),
    };

    var context = new LocalSearchContextBuilder(
            new LocalSearchContextOptions { ConversationHistoryMaxTurns = 2 })
        .Build(FullInputs() with { ConversationHistory = turns });

    // Two pairs kept (the oldest two), and only their user halves rendered.
    Assert.Equal(2, context.History.Rendered);
    Assert.Contains("question 1", context.Text, StringComparison.Ordinal);
    Assert.Contains("question 2", context.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("question 3", context.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("answer 1", context.Text, StringComparison.Ordinal);
}

[Fact]
public void AssistantTurnsRenderWhenUserTurnsOnlyIsOff()
{
    var turns = new List<ConversationTurn>
    {
        new(ConversationRole.User, "question 1"),
        new(ConversationRole.Assistant, "answer 1"),
    };

    var context = new LocalSearchContextBuilder(
            new LocalSearchContextOptions { IncludeUserTurnsOnly = false })
        .Build(FullInputs() with { ConversationHistory = turns });

    Assert.Contains("user|question 1", context.Text, StringComparison.Ordinal);
    Assert.Contains("assistant|answer 1", context.Text, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run all three to verify they fail**

Run: `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "FullyQualifiedName~LocalSearchContextBuilderTests"`
Expected: FAIL to compile — `ConversationTurn` does not exist. That is the correct first failure.

- [ ] **Step 5: Add the turn model**

Create `src/Rag.NET.GraphRag/LocalSearch/ConversationTurn.cs`:

```csharp
namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>Who spoke a turn.</summary>
public enum ConversationRole
{
    /// <summary>The user.</summary>
    User,

    /// <summary>The assistant.</summary>
    Assistant,

    /// <summary>A system instruction.</summary>
    System,
}

/// <summary>One turn of the conversation a local-search query arrives in.</summary>
/// <remarks>
/// Local search folds recent history into the context so a follow-up question resolves against
/// what was already said. See section 9 of
/// <c>docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md</c> for the reading of
/// upstream this follows.
/// </remarks>
/// <param name="Role">Who spoke.</param>
/// <param name="Content">What was said.</param>
public sealed record ConversationTurn(ConversationRole Role, string Content);
```

- [ ] **Step 6: Add the option, the input and the fill**

`LocalSearchContextOptions` — beside the other upstream defaults:

```csharp
/// <summary>Question-and-answer pairs from the conversation folded into the context. Default: 5.</summary>
/// <remarks>
/// <c>max_qa_turns</c> upstream, and it counts <b>pairs, not messages</b>: only a
/// <see cref="ConversationRole.User"/> turn opens a pair, and every turn after it up to the next
/// user turn belongs to that pair. History is assembled before the three proportions divide the
/// budget and its tokens come off the total first, so a long history shrinks every other section
/// rather than overrunning the budget.
/// </remarks>
[GreaterThanOrEqualTo(0)]
public int ConversationHistoryMaxTurns { get; set; } = 5;

/// <summary>Whether only the user's questions render. Default: <see langword="true"/>.</summary>
/// <remarks>
/// <c>include_user_turns_only</c> upstream, default <see langword="true"/> there and at the local
/// search call site. So by default the assistant's replies are grouped into pairs — which is what
/// the cap counts — and then dropped before rendering: pairing is visible in the arithmetic and
/// invisible in the output.
/// </remarks>
public bool IncludeUserTurnsOnly { get; set; } = true;

/// <summary>
/// Whether the cap keeps the newest pairs instead of the oldest. Default: <see langword="false"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Upstream's docstring and its only call site disagree, and the call site wins.</b>
/// <c>recency_bias</c> defaults to <see langword="true"/> in <c>conversation_history.py</c> and is
/// passed <see langword="false"/> by <c>mixed_context.py</c>, which is the only caller. The
/// truncation reverses the list only when the flag is set and then takes from the front, so at the
/// shipped value a conversation longer than the cap contributes its <b>beginning</b> rather than
/// its most recent exchanges.
/// </para>
/// <para>
/// Reproduced rather than corrected, for the reason
/// <see cref="EntityOversampleScaler"/> records about its own surprise: which turns reach the
/// context changes what the model is asked to answer from, so silently "fixing" it would make this
/// a different system that resembled the specification. Set it to <see langword="true"/> for the
/// behaviour most readers expect the parameter to have.
/// </para>
/// </remarks>
public bool ConversationHistoryRecencyBias { get; set; }
```

Use whichever ZeroAlloc.Validation attribute the assembly actually exposes for "at least zero" —
check the attributes already used in this file and in `GraphRagRetrievalOptions` before choosing.
Zero must be legal: it means "no history", not an error.

`LocalSearchInputs` — with a remark explaining that order is oldest-first (or whatever §9.2 says):

```csharp
/// <summary>The conversation this query arrives in, oldest turn first.</summary>
public IReadOnlyList<ConversationTurn> ConversationHistory { get; init; } = [];
```

`LocalSearchContext` — a `History` fill beside the other four:

```csharp
/// <summary>Conversation turns rendered, of those offered.</summary>
public required SectionFill History { get; init; }
```

Adding a `required` member breaks every existing construction of `LocalSearchContext`. There is
one, in `LocalSearchContextBuilder.Build`. Fix it there; do not relax `required`.

- [ ] **Step 7: Build the section, first, and subtract it first**

In `LocalSearchContextBuilder.Build`, replace the budget arithmetic:

```csharp
var (historyText, history) = BuildHistory(inputs, _options.MaxContextTokens);

// Spec section 2: history is built first and comes off the total before the proportions are
// applied. Applying them to the full total and prepending history afterwards overruns the
// budget by exactly the history's size.
var total = Math.Max(_options.MaxContextTokens - history.Tokens, 0);
var communityBudget = Slice(total, _options.CommunityProportion);
var sourceBudget = Slice(total, _options.TextUnitProportion);
var localBudget = Slice(total, 1.0 - _options.CommunityProportion - _options.TextUnitProportion);

var (reportText, reports) = BuildReports(inputs, communityBudget);
var (localText, entities, relationships) = BuildLocal(inputs, localBudget);
var (sourceText, sources) = BuildSources(inputs, sourceBudget);

var text = Join(historyText, reportText, localText, sourceText);
```

And add `BuildHistory`, following `BuildReports`' shape exactly — `ContextTable`, `TryAdd`, break
on a row that does not fit, `SectionFill` on the way out:

```csharp
/// <summary>Builds the conversation-history section, which precedes every other.</summary>
/// <remarks>
/// <para>
/// Two columns, per spec §9.3: <c>turn</c> holds the role string and <c>content</c> the text.
/// There is no third column and no turn index — upstream renders a two-column pandas frame whose
/// <c>turn</c> column <i>is</i> the role.
/// </para>
/// <para>
/// The cap is applied to QA pairs before the budget is consulted, and at the shipped
/// <see cref="LocalSearchContextOptions.ConversationHistoryRecencyBias"/> of
/// <see langword="false"/> it keeps the <b>oldest</b> pairs.
/// </para>
/// <para>
/// <b>Deviation.</b> When not even the first pair fits, upstream still emits the banner with no
/// rows under it and still charges its tokens to the budget. This returns an empty section
/// instead, because <see cref="ContextTable"/> makes that choice repository-wide and states why:
/// a banner with nothing under it tells the model the section exists and is empty, which is a
/// claim about the conversation rather than about the budget. The difference is a handful of
/// tokens in a case where the history was already too large to use.
/// </para>
/// </remarks>
/// <param name="inputs">Graph material and the conversation.</param>
/// <param name="budget">
/// Tokens this section may spend — the whole context budget, since it is taken off the top rather
/// than allocated a proportion.
/// </param>
/// <returns>Rendered section and its fill.</returns>
private (string Text, SectionFill Fill) BuildHistory(LocalSearchInputs inputs, int budget)
{
    var pairs = GroupIntoQaTurns(inputs.ConversationHistory);
    if (pairs.Count == 0 || _options.ConversationHistoryMaxTurns == 0)
    {
        return (string.Empty, new SectionFill(0, pairs.Count, 0, budget));
    }

    // Upstream reverses only when recency bias is on, then takes from the front either way.
    if (_options.ConversationHistoryRecencyBias)
    {
        pairs.Reverse();
    }

    if (pairs.Count > _options.ConversationHistoryMaxTurns)
    {
        pairs.RemoveRange(
            _options.ConversationHistoryMaxTurns,
            pairs.Count - _options.ConversationHistoryMaxTurns);
    }

    var table = new ContextTable(
        "Conversation History", ["turn", "content"], _options.ColumnDelimiter, budget);

    foreach (var pair in pairs)
    {
        if (!table.TryAdd("user", Clean(pair.Question.Content)))
        {
            break;
        }

        if (_options.IncludeUserTurnsOnly || pair.Answers.Count == 0)
        {
            continue;
        }

        var answered = string.Join("\n", pair.Answers.Select(a => a.Content));
        if (!table.TryAdd("assistant", Clean(answered)))
        {
            break;
        }
    }

    return (table.Render(), new SectionFill(table.Rendered, pairs.Count, table.Tokens, budget));
}

/// <summary>Groups a flat turn list into question-and-answer pairs, oldest first.</summary>
/// <remarks>
/// Spec §9.1's <c>to_qa_turns</c>, reproduced including its edge: <b>only a
/// <see cref="ConversationRole.User"/> turn opens a pair</b>, and every turn until the next user
/// turn is appended to the open pair's answers — a <see cref="ConversationRole.System"/> turn
/// among them included, which therefore renders under the literal string <c>assistant</c>. Turns
/// preceding the first user turn belong to no pair and are dropped, as upstream drops them.
/// </remarks>
/// <param name="turns">The conversation, oldest first.</param>
/// <returns>The pairs, oldest first.</returns>
private static List<QaTurn> GroupIntoQaTurns(IReadOnlyList<ConversationTurn> turns)
{
    var pairs = new List<QaTurn>();
    QaTurn? open = null;

    for (var i = 0; i < turns.Count; i++)
    {
        if (turns[i].Role == ConversationRole.User)
        {
            if (open is not null)
            {
                pairs.Add(open);
            }

            open = new QaTurn(turns[i]);
        }
        else
        {
            open?.Answers.Add(turns[i]);
        }
    }

    if (open is not null)
    {
        pairs.Add(open);
    }

    return pairs;
}
```

`QaTurn` is a small private nested type — `sealed class QaTurn(ConversationTurn question)` with
`Question` and a `List<ConversationTurn> Answers`. Keep it `private` inside the builder: it is
grouping scaffolding, not part of the package's surface, and nothing outside the builder needs it.

**One rendering detail spec §9.6 flags as unverified:** upstream renders through
`pandas.to_csv`, whose default `QUOTE_MINIMAL` wraps any cell containing the delimiter, a quote or
a newline in double quotes. §9.6 could not execute pandas to confirm the exact bytes. Do **not**
implement CSV quoting on that basis — `ContextTable` does not quote for any other section, and
inventing quoting here on an unverified detail would make history the only section rendered by
different rules. Note it as a known open difference in the `<remarks>` and move on.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "FullyQualifiedName~LocalSearchContextBuilderTests"`
Expected: PASS, all three new tests and every existing one. Existing tests that construct
`LocalSearchContext` or assert on total token counts may need the new `History` fill; update them,
and if any existing assertion about budgets changes value, say why in the commit body.

- [ ] **Step 9: Commit**

```bash
git add src/Rag.NET.GraphRag/LocalSearch tests/Rag.NET.GraphRag.Tests/LocalSearch
git commit -m "feat(graphrag): conversation history in the local search context"
```

---

### Task 4: Conversation history through the entry point

**Files:**
- Modify: `src/Rag.NET.GraphRag/LocalSearch/IGraphRagSearch.cs`
- Modify: `src/Rag.NET.GraphRag/LocalSearch/GraphRagSearch.cs`
- Test: `tests/Rag.NET.GraphRag.Tests/LocalSearch/GraphRagSearchTests.cs`

**Interfaces:**
- Consumes: `ConversationTurn`, `LocalSearchInputs.ConversationHistory` from Task 3; spec §9.5 for
  whether history joins the query before entity selection.
- Produces:
  - `Task<LocalSearchContext> BuildLocalContextAsync(string query, IReadOnlyList<ConversationTurn> history, CancellationToken cancellationToken = default)`
  - `Task<LocalSearchAnswer> LocalSearchAsync(string query, IReadOnlyList<ConversationTurn> history, CancellationToken cancellationToken = default)`

  The two-argument forms stay, delegating with an empty history.

- [ ] **Step 1: Confirm the interface can still change shape**

`IGraphRagSearch` shipped in #321 on 2026-08-19 and the published packages are at 0.1.0 from
2026-08-11, so it is not in any released package and adding members needs no obsolete cycle.
Verify before relying on it:

```bash
gh release list --limit 5
git tag -l | tail -5
```
Expected: no tag at or after 2026-08-18. If a release did go out, add the overloads as **default
interface methods** instead of abstract ones, so existing implementers still compile.

- [ ] **Step 2: Write the failing test**

Add to `tests/Rag.NET.GraphRag.Tests/LocalSearch/GraphRagSearchTests.cs`:

```csharp
[Fact]
public async Task HistoryReachesTheAssembledContext()
{
    await using var fixture = await Fixture.CreateAsync();

    var context = await fixture.Search.BuildLocalContextAsync(
        "And who measured it?",
        [new ConversationTurn(ConversationRole.User, "What did ÅNGSTRÖM work on?")],
        TestContext.Current.CancellationToken);

    Assert.Equal(1, context.History.Rendered);
    Assert.Contains("What did ÅNGSTRÖM work on?", context.Text, StringComparison.Ordinal);
}
```

Three conventions taken from the file rather than invented: `Fixture.CreateAsync()` (line 205)
seeds a real `SqliteGraphStore` and `InMemoryVectorStore` and exposes `Search`; the cancellation
token is `TestContext.Current.CancellationToken`, passed explicitly on every await; and test
names are PascalCase without underscores. **Task 3's tests should follow the same naming** —
the underscored names written there are wrong for this codebase; rename them to
`HistoryTokensComeOffTheTotalBeforeTheProportions`,
`HistoryIsTheFirstSectionInTheContext` and `OnlyTheMostRecentTurnsAreKept`.

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj --filter "FullyQualifiedName~HistoryReachesTheAssembledContext"`
Expected: FAIL to compile — no three-argument overload.

- [ ] **Step 4: Add the overloads to the interface**

In `IGraphRagSearch.cs`, add both members with full doc comments, including a
`<param name="history">` that says turns are oldest-first and that only
`LocalSearchContextOptions.ConversationHistoryMaxTurns` of them reach the context.

- [ ] **Step 5: Implement them**

In `GraphRagSearch.cs`, make the existing two-argument methods delegate:

```csharp
/// <inheritdoc/>
public Task<LocalSearchContext> BuildLocalContextAsync(
    string query, CancellationToken cancellationToken = default) =>
    BuildLocalContextAsync(query, [], cancellationToken);
```

and move the body into the three-argument form, threading the history into `LocalSearchInputs`:

```csharp
var inputs = await CollectAsync(entities, cancellationToken).ConfigureAwait(false);
var context = _builder.Build(inputs with { ConversationHistory = history });
```

The early-return path for zero entities must carry the history too — a follow-up question whose
entities do not match is exactly when the history is the only context there is:

```csharp
if (entities.Count == 0)
{
    LogNoEntitiesMatched(_logger, query);
    return _builder.Build(new LocalSearchInputs
    {
        SelectedEntities = [],
        ConversationHistory = history,
    });
}
```

**Spec §9.5 settled this, and the answer is yes — it is no longer conditional.** `mixed_context.py`
concatenates the last `conversation_history_max_turns` **user** turns onto the query *before*
`map_query_to_entities`:

```python
if conversation_history:
    pre_user_questions = "\n".join(
        conversation_history.get_user_turns(conversation_history_max_turns)
    )
    query = f"{query}\n{pre_user_questions}"
```

So `SelectEntitiesAsync` must embed the folded query, not the bare one. Four things the source is
specific about, the last of which is easy to get backwards — `get_user_turns` at
`conversation_history.py:139`:

```python
def get_user_turns(self, max_user_turns: int | None = 1) -> list[str]:
    """Get the last user turns in the conversation history."""
    user_turns = []
    for turn in self.turns[::-1]:
        if turn.role == ConversationRole.USER:
            user_turns.append(turn.content)
            if max_user_turns and len(user_turns) >= max_user_turns:
                break
    return user_turns
```

1. **User turns only**, regardless of `IncludeUserTurnsOnly`.
2. **A separate path from the rendered table** — the same turns can steer selection without ever
   being shown to the model.
3. **The current query comes first**, with the history appended after it.
4. **It takes the LAST N user turns and returns them newest-first** — it walks the list reversed
   and appends. This is the opposite of `BuildHistory`'s selection in Task 3, which keeps the
   *oldest* pairs because `recency_bias=False`.

**Point 4 is worth stating twice, because the two halves of one function disagree.** The rendered
history section shows the conversation's beginning; the query fold uses its most recent questions,
newest first. Both are upstream's behaviour, in the same `build_context` call. Reproduce both; do
not make them consistent with each other.

```csharp
/// <summary>Folds recent user questions onto the query, as upstream does before entity selection.</summary>
/// <remarks>
/// <para>
/// Spec §9.5. A follow-up question ("and who signed it?") embeds to almost nothing on its own; the
/// preceding questions are what make it match an entity. Deliberately a different path from the
/// rendered history section — these turns steer selection whether or not they are shown to the
/// model, and they are user turns even when the section renders assistant turns too.
/// </para>
/// <para>
/// <b>The most recent questions, newest first</b> — <c>get_user_turns</c> walks the history
/// reversed and appends. Note that this disagrees with the rendered section, which keeps the
/// <i>oldest</i> pairs because its caller passes <c>recency_bias=False</c>. Both are upstream's
/// behaviour in the same call; they are not made consistent here.
/// </para>
/// </remarks>
private string FoldHistoryIntoQuery(string query, IReadOnlyList<ConversationTurn> history)
{
    if (history.Count == 0 || _options.ConversationHistoryMaxTurns == 0)
    {
        return query;
    }

    var questions = new List<string>();
    for (var i = history.Count - 1; i >= 0; i--)
    {
        if (history[i].Role != ConversationRole.User)
        {
            continue;
        }

        questions.Add(history[i].Content);
        if (questions.Count >= _options.ConversationHistoryMaxTurns)
        {
            break;
        }
    }

    return questions.Count == 0 ? query : query + "\n" + string.Join("\n", questions);
}
```

Add a test pinning that the folded text reaches the embedder — assert on what the embedding
generator was called with, so the test fails if the fold is dropped:

```csharp
[Fact]
public async Task HistoryIsFoldedIntoTheQueryBeforeEntitySelection()
{
    // Spec section 9.5. Selection sees the history even though the rendered section is built
    // separately — a follow-up question embeds to almost nothing on its own.
    await using var fixture = await Fixture.CreateAsync();

    var context = await fixture.Search.BuildLocalContextAsync(
        "and who measured it?",
        [new ConversationTurn(ConversationRole.User, "What did ÅNGSTRÖM work on?")],
        TestContext.Current.CancellationToken);

    Assert.True(context.Entities.Rendered > 0, "the folded query selected no entities");
}
```

If `Fixture`'s embedder is a real ONNX generator rather than a substitute, this assertion is the
honest one available; if it is a substitute you can capture arguments from, assert directly that
the embedded string contains both the question and the history, which is the stronger test. Read
the fixture and pick the stronger option it supports.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Rag.NET.GraphRag.Tests/Rag.NET.GraphRag.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.GraphRag/LocalSearch tests/Rag.NET.GraphRag.Tests/LocalSearch
git commit -m "feat(graphrag): conversation history on the IGraphRagSearch entry point"
```

---

### Task 5: The `localspec` arm in the MultiHop-RAG answer harness

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerArm.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/GraphRagRun.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs`
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagAnswerReproduction.cs`

**Interfaces:**
- Consumes: `IGraphRagSearch`, `LocalSearchContext.Text` from Tasks 3–4; `GraphRagRun`'s existing
  stores.
- Produces:
  - `AnswerArm.LocalSpec` — the string `"localspec"`
  - `GraphRagRun.LocalSpecContextAsync(string query, CancellationToken ct)` → `Task<string>`
  - a `MultiHopRagAnswerReproduction` entry with an **empty** accuracy list, which the harness
    already handles by printing what it measured and asserting nothing.

  Task 6 runs this arm and fills that entry.

**The shape problem, and the decision.** Every existing arm returns `IReadOnlyList<SearchResult>`,
which `RenderContext` turns into the `{context}` slot of the shared `PromptTemplate`. The new
local search returns a **rendered string** — that is the whole point of the separate entry point.
So this arm bypasses `RenderContext` and substitutes its context directly, **into the same
`PromptTemplate`**. Per Task 2 Step 3: holding the prompt constant is what makes the number
comparable to `dense`, `control` and `filtered`. Measuring `LocalSearchPrompt` as well is a
separate arm and a separate question — do not fold it in here.

- [ ] **Step 1: Add the arm constant**

In `AnswerArm.cs`, beside the others:

```csharp
/// <summary>
/// Microsoft's local search as specified: entity selection by description embedding, community
/// reports, an uncapped in-network relationship table, source chunks via entity provenance, all
/// under a 12,000-token budget — <c>IGraphRagSearch</c>, not the retrieval pipeline.
/// </summary>
/// <remarks>
/// <b>Not a variant of <see cref="Local"/>; a different thing with the same name upstream.</b>
/// That arm is the PageRank blend at weight 0.3 over dense candidates, which is not in
/// Microsoft's local search at all. This arm is what Milestone 5.2 believed it was measuring
/// when it concluded GraphRAG does not help on this corpus. Both are kept: the comparison
/// between them is the measurement.
/// </remarks>
public const string LocalSpec = "localspec";
```

and add it to `All`.

- [ ] **Step 2: Run the harness's own guard to see it fail**

`MultiHopRagAnswerReproduction.Find` throws for a pair with no entry — by design, so an arm
cannot exist without something pinning its figure.

Run: `dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~RequireRecordedCase"`
Expected: FAIL with "No answer reproduction is recorded for dataset 'multihop-rag' under the
localspec arm." If no test calls `RequireRecordedCase` over `AnswerArm.All`, add one — that guard
is what this step is exercising.

- [ ] **Step 3: Add the empty pin entry**

In `MultiHopRagAnswerReproduction.cs`:

```csharp
new(
    "multihop-rag",
    AnswerArm.LocalSpec,
    [],
    "NOT YET MEASURED. Microsoft's local search as specified -- IGraphRagSearch, the context " +
    "builder from #317/#320/#321 plus conversation history, at the upstream defaults " +
    "(12,000 tokens, 0.15 community / 0.5 text-unit, top-10 entities oversampled to 20, " +
    "covariates off). Its context replaces the top-6 rendering in the SAME PromptTemplate the " +
    "other arms use, so the only variable against dense/control/filtered is the context. " +
    "The figure this arm produces is what Milestone 5.2's -0.02761 conclusion should have been " +
    "measured against: 5.2 measured a PageRank blend over dense candidates, which is not local " +
    "search. Phase 6.x.7."),
```

- [ ] **Step 4: Run it to verify the guard passes**

Run: `dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~RequireRecordedCase"`
Expected: PASS.

- [ ] **Step 5: Build the context in `GraphRagRun`**

Add to `GraphRagRun.cs`, constructing `GraphRagSearch` over the run's existing graph store, graph
chunk store, document store and embedder — read the class first and reuse its fields rather than
opening new stores:

```csharp
/// <summary>The specification-faithful local search context for one query, rendered.</summary>
/// <remarks>
/// Constructed once and reused across queries: the builder is pure and the search holds only
/// store references, so a per-query instance would re-validate the options 2,556 times and
/// change nothing.
/// </remarks>
/// <param name="query">The question.</param>
/// <param name="ct">Cancels the embedding call and the store reads.</param>
/// <returns>The rendered context.</returns>
public async Task<string> LocalSpecContextAsync(string query, CancellationToken ct)
{
    var context = await _localSpecSearch.Value.BuildLocalContextAsync(query, ct);
    return context.Text;
}
```

with a `Lazy<GraphRagSearch>` field built from the run's stores and a `NullLogger`. The chat
client it takes is unused by `BuildLocalContextAsync`; pass the run's answering client so the
type is satisfied without a second dependency.

- [ ] **Step 6: Dispatch the arm**

In `BeirGraphRagAnswerTests.cs`, the arm loop currently maps every arm to an
`IReadOnlyList<SearchResult>` before rendering. Add the string path around it:

```csharp
var rendered = arm == AnswerArm.LocalSpec
    ? localSpecContexts[query.Id]
    : RenderContext(arm switch
    {
        AnswerArm.Local => localContexts[query.Id],
        AnswerArm.Control => controlContexts[query.Id],
        AnswerArm.Filtered => filteredContexts[query.Id],
        _ => await RetrieveContextAsync(arm, query.Text, run, articles, generator, embeddings, answering, token),
    });

var prompt = PromptTemplate
    .Replace("{question}", query.Text, StringComparison.Ordinal)
    .Replace("{context}", rendered, StringComparison.Ordinal);
```

and collect `localSpecContexts` in the same sequential pre-pass as the other graph-store arms —
`CollectGraphStoreContextsAsync` — for the reason its remark already gives: retrieving per arm in
the parallel phase would let a difference between arms be a difference in what was retrieved.

- [ ] **Step 7: Run the harness's fast path to verify it wires up**

Run:
```bash
RAGNET_GRAPHRAG_ANSWERS_ARMS=localspec RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES=5 \
  dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~BeirGraphRagAnswerTests"
```
Expected: the arm runs over 5 queries and prints "NO ANSWER REPRODUCTION RECORDED ... localspec"
with a measured accuracy, asserting nothing. If it needs `RAGNET_GRAPHRAG_ANSWERS_GENERATE` and
`OPENROUTER_API_KEY`, set them — 5 queries is a handful of completions. If the corpus is not
provisioned the test skips; that is fine here, and Task 6 is where provisioning matters.

- [ ] **Step 8: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests
git commit -m "test(benchmarks): the localspec arm — Microsoft's local search on the answer scale"
```

---

### Task 6: Run the measurement and pin it

**This is the point of all of it** (spec, Phases table). The 5.2 finding — *"GraphRAG does not
help on this corpus"* — was measured against a local search that had never implemented local
search. This task replaces that with a number about local search.

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/MultiHopRagAnswerReproduction.cs`
- Modify: `src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj` — the `VerifiedBy` provenance comment
- Modify: `docs/guide/graphrag.md`
- Modify: `docs/planning/ROADMAP.md` — Phase 6.2.1's status line

**Interfaces:**
- Consumes: everything above.
- Produces: the pinned accuracy for `localspec`, and the answer to whether local search beats
  dense when it is not starving the model.

- [ ] **Step 1: Confirm the preconditions before spending anything**

The run needs: the provisioned MultiHop-RAG corpus, the restored answer cache, `OPENROUTER_API_KEY`,
and — per the roadmap's standing note on the #300 follow-up — **an idle machine**, because three
timing runs on 2026-08-17 disagreed by 6× on identical inputs. Accuracy is not a timing figure and
is exact on a cache replay, so an idle machine matters for the cost and duration lines, not the
accuracy. Record the machine, OS and .NET version; the pin's provenance string carries them.

- [ ] **Step 2: Pilot on 100 queries**

```bash
RAGNET_GRAPHRAG_ANSWERS_GENERATE=1 RAGNET_GRAPHRAG_ANSWERS_ARMS=localspec \
RAGNET_GRAPHRAG_ANSWERS_MAX_QUERIES=100 \
  dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~BeirGraphRagAnswerTests"
```

Read the printed accuracy and the cache hit/miss split. **Stop and reconsider if the pilot shows
the context is empty or near-empty for most queries** — `LocalSearchContext`'s `SectionFill`s exist
precisely so a silently empty section is visible. A localspec context whose `Entities.Rendered` is
0 across the pilot means entity selection is not matching, and running the full sweep would buy an
expensive number about nothing.

- [ ] **Step 3: Run the full sweep**

```bash
RAGNET_GRAPHRAG_ANSWERS_GENERATE=1 RAGNET_GRAPHRAG_ANSWERS_ARMS=localspec \
  dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~BeirGraphRagAnswerTests"
```

Expect roughly one arm's worth of generation — the harness's own record puts a single new arm at
~2,556 requests, and the `filtered` arm needed only 109 completions because its contexts collided
with cached ones. **Do not assume that will repeat**: localspec's context is a rendered graph
context that no other arm has produced, so expect close to full generation, on the order of the
~$3 per derived arm the phase entry budgets.

- [ ] **Step 4: Pin the figure**

Replace the empty accuracy list in `MultiHopRagAnswerReproduction.cs` with the measured value and
write the provenance in the style of the entries around it — machine, OS, .NET version, date,
query and request counts, cache split, duration, model, temperature, the per-type breakdown
(inference / comparison / temporal / nulls), and **the comparisons that carry the meaning**:

- against `dense` 0.3499 — does the graph context beat article-only?
- against `control` 0.1384 — what does it do relative to unfiltered store pollution?
- against `local` 0.2102 — **the headline**: what did the specification cost or buy over the blend
  that Milestone 5.2 mistook for local search?
- against `global` 0.5951 — does local now reach the arm that beat it on entity questions?

Read the per-type numbers against the base rates the existing entries record: comparison gold is
60% yes and temporal 46% yes, so always-yes scores 0.598 and 0.463 there. A low figure on those
types is abstention, not error, and the entry must say which it is.

- [ ] **Step 5: Run the reproduction to verify the pin holds**

```bash
RAGNET_GRAPHRAG_ANSWERS_ARMS=localspec \
  dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --filter "FullyQualifiedName~BeirGraphRagAnswerTests"
```
Expected: PASS, replayed entirely from the answer cache — 0 generated. A miss means retrieval
handed the model a different context on this replay, which fails before any figure is computed.

- [ ] **Step 6: Update the guide and the ledger comment**

`docs/guide/graphrag.md` currently states — correctly, for the old behaviour — that local search
adds no candidates and that the blend was the whole −0.02761. Add what this run measured, and
**do not delete the old finding**: the guide's value is that it records what was believed and what
replaced it. Keep the 2,255-of-2,255 result as history and say the behaviour it describes is now
`[Obsolete]`.

Update the `<!-- benchmark: ... -->` comment above `<VerifiedBy>benchmark</VerifiedBy>` in
`src/Rag.NET.GraphRag/Rag.NET.GraphRag.csproj` to name the localspec figure alongside the existing
four.

- [ ] **Step 7: Update the roadmap and commit**

Rewrite Phase 6.2.1's status line in `docs/planning/ROADMAP.md` to record what the sweep found,
and — per Task 1 Step 8 — either delete `GraphLocalSearchBehavior` now that a replacement figure
exists, or restate why it is still retained.

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests src/Rag.NET.GraphRag docs
git commit -m "bench(graphrag): Microsoft's local search, measured on MultiHop-RAG"
```

The body carries the figure, the four comparisons, the cost and the machine — the same shape as
the `filtered` arm's commit, which is the model for this one.

---

## Follow-ups this plan deliberately does not do

Recorded here with their origin so they get scheduled rather than accumulating as open notes.

- **6.x.5 covariates** — own plan, for the two reasons in the Scope section. Needs a
  `GraphCovariate` model, a `SqliteGraphStore` table and migration, an extraction pass, a context
  section, and the quadratic per-entity rebuild loop `LocalSearchContextBuilder.BuildLocal`'s
  remark says a covariate section forces.
- **Deleting `GraphLocalSearchBehavior` and `PageRankWeight`** — Task 1 Step 8 schedules it into
  6.2.1, gated on Task 6's figure existing.
- **`LocalSearchPrompt` as its own arm** — Task 2 Step 3 holds the prompt constant so the context
  is the only variable. Whether upstream's prompt beats the harness's on the same context is a
  real question and a separate arm.
- **The rest of Phase 6.2.1** — RAPTOR, HyDE, hybrid, reranking, late chunking, SPLADE, the three
  answer engines, per-store SciFact parity, the pipeline-parity test, #176. Each its own plan.
