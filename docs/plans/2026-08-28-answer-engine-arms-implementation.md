# Answer-Engine Arms Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add five answer-engine arms to the 5.2.2 answer harness — a `ChatAnswerEngine` control plus MapReduce, Refine and two FLARE variants — so Milestone 6's "three answer engines measured" clause has arms to run, with the pilot gates that make a later run trustworthy.

**Architecture:** Every new arm is **`dense` retrieval + a different generation strategy**. The arms reuse the existing `AnswerArm.Dense` retrieval case verbatim, so retrieval is held fixed by sharing the code path. The engines receive the harness's existing `CachedGraphRagClient` as their `IChatClient` and build their own prompts, so their cache entries are new keys and no pinned figure is disturbed. The current inline `PromptTemplate` is never edited.

**Tech Stack:** .NET 10, C#, xunit.v3 (`TestContext.Current.CancellationToken`, `Assert.SkipUnless`), `Microsoft.Extensions.AI` (`IChatClient`, `ChatMessage`), `Microsoft.Extensions.Logging.Abstractions` (`NullLogger<T>`).

**Spec:** [`2026-08-28-answer-engine-arms-design.md`](2026-08-28-answer-engine-arms-design.md)

## Global Constraints

- **Everything lands in `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`.** No new test project. That project already runs in the fast tier with expensive cases self-skipping.
- **NEVER edit `BeirGraphRagAnswerTests.PromptTemplate`.** The answer cache is keyed on its text. One character rekeys every entry, costs three pinned answer figures (`dense` 0.350, `global` 0.595, and the RAPTOR arms) and roughly $9 of warm cache. If a task seems to need it changed, STOP and report.
- **Do not modify `src/`.** The engines are used as they ship. This thread measures them; it does not improve them.
- `Directory.Build.props` sets `TreatWarningsAsErrors=true`; nullable is on; Meziantou analyzers are active (MA0002 concrete collections in `Assert.Equal`; MA0006 `string.Equals(…, StringComparison.Ordinal)`; MA0015 explicit `paramName`; MA0051 caps methods at 60 lines — extract a helper); ZeroAlloc's ZA0601 forbids LINQ inside loops.
- **Arm names are lowercase, no separators**, matching the existing constants: `chatengine`, `mapreduce`, `refine`, `flare`, `flarefixed`.
- **Top-6 context.** `BeirGraphRagAnswerTests.ContextChunks` is `6`; use the constant, never a literal.
- Conventional commits, header ≤ 100 characters. CI lints every commit a PR adds.

## Interfaces this plan uses (verified against the repo)

```csharp
// Rag.NET.Abstractions
Task<RagResponse> IAnswerEngine.AskAsync(
    string query, IReadOnlyList<SearchResult> sources,
    RagOptions? options = null, CancellationToken cancellationToken = default);

public sealed record RagResponse { public required string Answer { get; init; }
                                   public required IReadOnlyList<SearchResult> Sources { get; init; } }

Task<Result<IReadOnlyList<SearchResult>, RagError>> IRetriever.RetrieveAsync(
    string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);

// Engine constructors
new ChatAnswerEngine(IChatClient chatClient, IConversationMemory? memory = null,
                     IContextualCompressor? compressor = null, IPromptObserver? promptObserver = null,
                     IGuidProvider? guidProvider = null);
new MapReduceAnswerEngine(IChatClient chatClient, ILogger<MapReduceAnswerEngine> logger,
                          IConversationMemory? memory = null, IContextualCompressor? compressor = null);
new RefineAnswerEngine(IChatClient chatClient, ILogger<RefineAnswerEngine> logger,
                       IConversationMemory? memory = null, IContextualCompressor? compressor = null);
new FlareAnswerEngine(IChatClient chatClient, IRetriever retriever, IConfidenceScorer scorer,
                      FlareOptions options, ILogger<FlareAnswerEngine>? logger = null);
```

`MapReduceAnswerEngine` and `RefineAnswerEngine` take a **required, non-nullable** logger — pass `NullLogger<T>.Instance`.

---

### Task 1: The arm constants

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerArm.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AnswerArm.ChatEngine`, `AnswerArm.MapReduce`, `AnswerArm.Refine`, `AnswerArm.Flare`, `AnswerArm.FlareFixed` — all `public const string` — each added to `AnswerArm.All`.

- [ ] **Step 1: Read the file and match its documentation register**

Open `AnswerArm.cs`. Every existing constant carries an XML doc comment explaining **what the arm is and what its difference against another arm means** — not just a restatement of the name. Match that. A one-line `<summary>The mapreduce arm.</summary>` is a defect here.

- [ ] **Step 2: Add the five constants**

```csharp
    /// <summary>
    /// Dense retrieval, answered by the shipped <c>ChatAnswerEngine</c> in one call — <b>the
    /// control for every other engine arm</b>.
    /// <para>
    /// Each engine builds its own prompts, so differencing an engine against <see cref="Dense"/>
    /// would bundle the mechanism with a prompt change and no result could say which caused what.
    /// This arm is single-shot through the same routing, so <c>chatengine − dense</c> is the prompt
    /// effect alone and <c>&lt;engine&gt; − chatengine</c> is the mechanism alone.
    /// </para>
    /// </summary>
    public const string ChatEngine = "chatengine";

    /// <summary>
    /// Dense retrieval, answered by <c>MapReduceAnswerEngine</c>: one call per context chunk, then
    /// one reduce over their outputs. Seven calls at top-6, but roughly a single-shot answer's token
    /// count, because each map call carries one chunk rather than all six.
    /// </summary>
    public const string MapReduce = "mapreduce";

    /// <summary>
    /// Dense retrieval, answered by <c>RefineAnswerEngine</c>: an initial answer from the first
    /// chunk, then one sequential rewrite per remaining chunk. Six calls at top-6.
    /// </summary>
    public const string Refine = "refine";

    /// <summary>
    /// Dense retrieval, answered by <c>FlareAnswerEngine</c> <b>as shipped</b> — sentence by
    /// sentence, re-retrieving mid-generation whenever a sentence scores below
    /// <c>ConfidenceThreshold</c>.
    /// <para>
    /// <b>This arm does not hold retrieval fixed</b>, which is why <see cref="FlareFixed"/> exists
    /// beside it: <c>flare − flarefixed</c> is what the lookahead buys, and it is the only
    /// difference here that is not purely a generation difference.
    /// </para>
    /// </summary>
    public const string Flare = "flare";

    /// <summary>
    /// Dense retrieval, answered by <c>FlareAnswerEngine</c> with <c>MaxRetrievals = 0</c> — the
    /// sentence-by-sentence mechanism with lookahead off, so retrieval is held fixed and the arm is
    /// comparable to <see cref="MapReduce"/> and <see cref="Refine"/>.
    /// </summary>
    public const string FlareFixed = "flarefixed";
```

- [ ] **Step 3: Add them to `All`**

Append all five to the `All` list, after the existing entries. Keep the file's existing ordering convention.

- [ ] **Step 4: Build**

```
dotnet build Rag.NET.slnx
```

Expected: PASS, 0 warnings. If `AnswerArm` has a test asserting `All`'s length or contents, update it — that is in scope for this task.

- [ ] **Step 5: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerArm.cs
git commit -m "test(arms): add the five answer-engine arm names"
```

---

### Task 2: The engine factory and the throwing stub retriever

> **AS BUILT — this task's design was wrong, three times over. The code below is kept as the record
> of what was planned; what shipped differs, and the differences are the point.**
>
> The plan is not rewritten in place, because the sequence of failures is more instructive than any
> one of the corrections. The steps below still show the first version.
>
> 1. **A stub that throws is not a guarantee.** `FlareAnswerEngine.TryLookaheadRetrievalAsync` wraps
>    the retriever call in a catch-all that logs and returns as if the lookahead found nothing. The
>    `InvalidOperationException` below vanishes into it; the engine answers, the call count is
>    unchanged, and a test asserting "it threw" or "no extra calls happened" passes while lookahead
>    has actually fired. **Correction:** `UnreachableRetriever.WasCalled`, set *before* the throw and
>    therefore immune to the swallow. The throw stays, because it is still right anywhere it is not
>    caught, but it proves nothing on its own.
> 2. **A flag nobody can read is not a guarantee either.** The harness then handed `flarefixed` the
>    same real retriever it built for `flare`, so `Create`'s `retriever ?? new UnreachableRetriever()`
>    never substituted the stub. The flag was not merely unread but absent — in exactly the run that
>    matters, the one with both arms selected, which is the only run `flare − flarefixed` can be
>    computed from. **Correction:** an `EngineRetrievers` holder in `BeirGraphRagAnswerTests`, which
>    owns *one* stub instance for the run, returns it for `flarefixed` and never returns the real
>    retriever for that arm whatever else is selected, and exposes `AssertLookaheadStayedOff()` to
>    read the flag after the last answer.
> 3. **The `??` fallback itself was the hazard.** Even once dead in-repo, `retriever ?? new
>    UnreachableRetriever()` would, if ever taken, build a stub nobody held a reference to —
>    reinstating precisely the unobservability of (2). **Correction:** `Create` now calls
>    `ArgumentNullException.ThrowIfNull(retriever)` for `flarefixed` as it already did for `flare`,
>    and uses the passed instance. `FlareFixed_RequiresARetriever` pins that.
>
> Two further departures from the code below, from the same review:
>
> - **`NullLogger` is gone.** Every engine is built with `AnswerEngineArms.FailureLog`, a counting
>   `ILogger`. `MapReduceAnswerEngine`, `RefineAnswerEngine` and `SelfAssessmentConfidenceScorer` all
>   swallow failures into their logger and answer from less than they were given, so with a null
>   logger a missing cache entry on a replay would silently degrade an arm's accuracy figure — and
>   Gate 2 would still pass, because its counter increments before the request is forwarded. The
>   harness now asserts **no exception was swallowed** and prints both counts in the cost block.
>   Warnings that carry no exception — an unparsable confidence score, an error result from
>   retrieval — are counted and printed but do not fail the run: they are the model's output rather
>   than a fault, and an unparsable reply is itself cached, so failing on one would make every
>   subsequent replay fail identically.
> - **`Create`'s signature gained that counter**, and is
>   `Create(string arm, IChatClient chatClient, IRetriever? retriever, FailureLog failures)`. It is
>   required rather than defaulted: a caller that could omit it would be back to discarding the logs.

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerEngineArms.cs`

**Interfaces:**
- Consumes: `AnswerArm` constants (Task 1).
- Produces:
  - `internal static IAnswerEngine Create(string arm, IChatClient chatClient, IRetriever? retriever)` — throws `ArgumentOutOfRangeException` for an arm it does not build.
  - `internal sealed class UnreachableRetriever : IRetriever` — throws `InvalidOperationException` from `RetrieveAsync`.

- [ ] **Step 1: Write the factory**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Builds the answer engine each engine arm generates with, over the harness's own answering
/// client.
/// <para>
/// Every engine receives the shared <c>CachedGraphRagClient</c> and builds its own prompts. The
/// answer cache is keyed on prompt text, so each engine's prompts are new keys and no existing
/// entry is touched — which is what keeps <c>dense</c>, <c>global</c> and the RAPTOR arms
/// reproducible while these arms are added.
/// </para>
/// </summary>
internal static class AnswerEngineArms
{
    /// <summary>
    /// Creates the engine for <paramref name="arm"/>.
    /// </summary>
    /// <param name="arm">One of the engine arms; anything else throws.</param>
    /// <param name="chatClient">The harness's answering client, shared by every arm.</param>
    /// <param name="retriever">
    /// Required by <see cref="AnswerArm.Flare"/> only, whose lookahead retrieves mid-generation.
    /// <see cref="AnswerArm.FlareFixed"/> is given an <see cref="UnreachableRetriever"/> instead:
    /// at <c>MaxRetrievals = 0</c> the retriever cannot be reached, so a stub that throws turns
    /// "lookahead is off" from an observation into a structural guarantee.
    /// </param>
    public static IAnswerEngine Create(string arm, IChatClient chatClient, IRetriever? retriever)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        if (string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal))
        {
            return new ChatAnswerEngine(chatClient);
        }

        if (string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal))
        {
            return new MapReduceAnswerEngine(chatClient, NullLogger<MapReduceAnswerEngine>.Instance);
        }

        if (string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal))
        {
            return new RefineAnswerEngine(chatClient, NullLogger<RefineAnswerEngine>.Instance);
        }

        if (string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal))
        {
            return new FlareAnswerEngine(
                chatClient,
                new UnreachableRetriever(),
                new SelfAssessmentConfidenceScorer(chatClient),
                new FlareOptions { MaxRetrievals = 0 });
        }

        if (string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(retriever);
            return new FlareAnswerEngine(
                chatClient,
                retriever,
                new SelfAssessmentConfidenceScorer(chatClient),
                new FlareOptions());
        }

        throw new ArgumentOutOfRangeException(
            nameof(arm), arm, "Not an arm this factory builds an engine for.");
    }

    /// <summary>
    /// An <see cref="IRetriever"/> that throws if it is ever called.
    /// </summary>
    /// <remarks>
    /// <see cref="AnswerArm.FlareFixed"/>'s whole claim is that lookahead is off. A counter reading
    /// zero and a code path that cannot execute are different guarantees, and this is the second
    /// one: if a future change ever reaches the retriever, the arm fails loudly instead of quietly
    /// retrieving and reporting as a fixed-context arm.
    /// </remarks>
    internal sealed class UnreachableRetriever : IRetriever
    {
        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "flarefixed retrieved mid-generation. MaxRetrievals is 0, so this is unreachable " +
                "unless FLARE's lookahead guard changed — the arm is no longer holding retrieval " +
                "fixed and its comparison against mapreduce/refine is invalid.");
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build Rag.NET.slnx
```

Expected: PASS, 0 warnings. The `using` list is a best guess — fix any that are wrong by locating the real namespace with `grep -rn "class SelfAssessmentConfidenceScorer" src --include=*.cs`, and the same for `ChatAnswerEngine` and `FlareOptions`. Do not work around a wrong namespace by duplicating a type.

- [ ] **Step 3: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerEngineArms.cs
git commit -m "test(arms): build each engine arm over the shared answering client"
```

---

### Task 3: The call-shape tests — the cost model, verified without spending anything

This is the task that makes the whole thread affordable to plan. The full sweep's cost is dominated by how many calls each engine makes per query, and that number is checkable here, with no corpus, no model and no money.

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerEngineArmsTests.cs`

**Interfaces:**
- Consumes: `AnswerEngineArms.Create`, `AnswerArm` constants.
- Produces: nothing later tasks consume.

- [ ] **Step 1: Write the counting fake and the tests**

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Pins each engine arm's <b>call shape</b> — how many LLM calls it makes for a top-6 context.
/// <para>
/// This is the cost model for a sweep of 2,556 queries, checked with a fake client instead of a
/// bill. If <c>mapreduce</c> ever makes one call it is not doing map-reduce; if it makes forty, the
/// sweep is mispriced. Phase 6.2.1's RAPTOR plan had no equivalent check, which is how an
/// eight-hour estimate built on the wrong workload's rate survived into a plan.
/// </para>
/// </summary>
public sealed class AnswerEngineArmsTests
{
    private const int ContextChunks = 6;

    [Fact]
    public async Task ChatEngine_MakesExactlyOneCall()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.ChatEngine, client, retriever: null);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task MapReduce_MakesOneCallPerChunkPlusOneReduce()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.MapReduce, client, retriever: null);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ContextChunks + 1, client.Calls);
    }

    [Fact]
    public async Task Refine_MakesOneCallPerChunk()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.Refine, client, retriever: null);

        _ = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ContextChunks, client.Calls);
    }

    /// <summary>
    /// The arm's defining claim, asserted structurally: at <c>MaxRetrievals = 0</c> the retriever is
    /// unreachable, so <see cref="AnswerEngineArms.UnreachableRetriever"/> never throws.
    /// </summary>
    [Fact]
    public async Task FlareFixed_NeverRetrieves()
    {
        var client = new CountingChatClient();
        var engine = AnswerEngineArms.Create(AnswerArm.FlareFixed, client, retriever: null);

        var response = await engine.AskAsync(
            "q", Sources(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.True(client.Calls >= 1, "flarefixed made no LLM call at all.");
    }

    [Fact]
    public void Flare_RequiresARetriever()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentNullException>(
            () => AnswerEngineArms.Create(AnswerArm.Flare, client, retriever: null));
    }

    [Fact]
    public void Create_RejectsAnArmItDoesNotBuild()
    {
        var client = new CountingChatClient();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => AnswerEngineArms.Create(AnswerArm.Dense, client, retriever: null));
    }

    private static IReadOnlyList<SearchResult> Sources()
    {
        var sources = new SearchResult[ContextChunks];
        for (var i = 0; i < ContextChunks; i++)
        {
            sources[i] = new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = FormattableString.Invariant($"context chunk {i}"),
                    DocumentId = new DocumentId(FormattableString.Invariant($"doc-{i}")),
                    ChunkIndex = 0,
                },
                Score = 1.0 - (i * 0.01),
            };
        }

        return sources;
    }

    /// <summary>Counts calls and returns a short fixed answer, so no engine loops on empty output.</summary>
    private sealed class CountingChatClient : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The arms use AskAsync, not streaming.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 2: Run and read the numbers before trusting them**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: all six PASS. **If `MapReduce_MakesOneCallPerChunkPlusOneReduce` or `Refine_MakesOneCallPerChunk` fails, do NOT adjust the expected number to match.** The counts come from reading the engines (`MapOneAsync` is one call per source plus a reduce; `RefineAnswerEngine` is one initial plus `sources.Count - 1`), and a mismatch means either the engines changed or the plan misread them. Report the actual count and stop.

`FlareFixed_NeverRetrieves` deliberately asserts only that it ran and made at least one call — FLARE's exact sentence count depends on when the model emits its done-token, and pinning it to a number would make this test a hostage to prompt wording. **The structural claim it makes is that `UnreachableRetriever` did not throw.**

- [ ] **Step 3: Record the observed FLARE call count**

Add the observed `client.Calls` for `flarefixed` to the test's XML doc as a comment — this is the first real datum on FLARE's sentence count, which the design names as the one unknown that swings the sweep's cost tenfold. Note it is against a fake client returning a fixed answer, so it is an upper-bound signal, not the corpus's real behaviour.

- [ ] **Step 4: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerEngineArmsTests.cs
git commit -m "test(arms): pin each engine arm's call shape with a counting fake client"
```

---

### Task 4: Wire the arms into the harness

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs`

**Interfaces:**
- Consumes: `AnswerArm` constants (Task 1), `AnswerEngineArms.Create` (Task 2).
- Produces: engine arms answerable by the harness.

**Read before editing:** `AnswerAllAsync` (around line 915) builds a *rendered context string* and sends it through `PromptTemplate`. Engines instead take `IReadOnlyList<SearchResult>` directly. So the sources must be obtained before the render, and the render must happen only on the non-engine path.

- [ ] **Step 1: Add the engine arms to the retrieval switch**

In `RetrieveContextAsync`, the five engine arms retrieve exactly as `AnswerArm.Dense` does. Add them to the same case rather than duplicating the body:

```csharp
            case AnswerArm.Dense:
            case AnswerArm.ChatEngine:
            case AnswerArm.MapReduce:
            case AnswerArm.Refine:
            case AnswerArm.Flare:
            case AnswerArm.FlareFixed:
                var vectors = await BeirHarness.EmbedAsync(generator, embeddings, [query], ct);
                return await articles.SearchAsync(vectors[0], new SearchOptions { TopK = ContextChunks }, ct);
```

Sharing the case is the point: it is what makes "retrieval is held fixed" true by construction rather than by two code paths agreeing.

- [ ] **Step 2: Branch generation on the arm**

In `AnswerAllAsync`, replace the render-then-prompt block so engine arms call their engine. The existing non-engine path must stay byte-identical — same `PromptTemplate`, same `GetResponseAsync` call — or every cached answer is orphaned:

```csharp
                    var sources = string.Equals(arm, AnswerArm.LocalSpec, StringComparison.Ordinal)
                        ? localSpecContexts[query.Id]
                        : arm switch
                        {
                            AnswerArm.Local => localContexts[query.Id],
                            AnswerArm.Control => controlContexts[query.Id],
                            AnswerArm.Filtered => filteredContexts[query.Id],
                            _ => await RetrieveContextAsync(
                                arm, query.Text, run, articles, corpusRun, perDocumentRun,
                                generator, embeddings, answering, _output, token),
                        };

                    string answerText;
                    if (AnswerEngineArms.IsEngineArm(arm))
                    {
                        var engine = AnswerEngineArms.Create(arm, answering, engineRetriever);
                        var engineResponse = await engine.AskAsync(
                            query.Text, sources, cancellationToken: token);
                        answerText = engineResponse.Answer;
                    }
                    else
                    {
                        var prompt = PromptTemplate
                            .Replace("{question}", query.Text, StringComparison.Ordinal)
                            .Replace("{context}", RenderContext(sources), StringComparison.Ordinal);

                        var response = await answering.GetResponseAsync(
                            [new ChatMessage(ChatRole.User, prompt)], cancellationToken: token);
                        answerText = response.Text ?? string.Empty;
                    }

                    tallies[arm].Record(arm, query.Id, expected, answerText);
```

Note `localSpecContexts` already holds rendered strings in the current code — check whether it stores `SearchResult`s or a rendered string, and keep its existing behaviour exactly. If it stores a string, keep it on the non-engine path only and leave its handling untouched.

- [ ] **Step 3: Add `IsEngineArm`**

Add to `AnswerEngineArms`:

```csharp
    /// <summary>Reports whether <paramref name="arm"/> generates through an <see cref="IAnswerEngine"/>.</summary>
    public static bool IsEngineArm(string arm) =>
        string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal);
```

- [ ] **Step 4: Supply `engineRetriever`**

`flare` needs a real `IRetriever` over the same `articles` store. **PR #414 (`feat/pipeline-parity-test`) builds exactly this adapter and proves it retrieves identically to the harness's dense row; it is open at the time of writing.**

If #414 has merged: build the retriever from a real `AddRagNet` pipeline over `articles`, following `PipelineParity.RetrieveThroughPipelineAsync`, and resolve `IRetriever` from the container.

If #414 has **not** merged: pass `null` and **exclude `flare` from the runnable arms**, leaving `flarefixed` and the other three. Record the exclusion in the test's XML doc and report it — do NOT hand-roll a second adapter whose equivalence to the harness is unproven, and do NOT silently let `flare` run with a stub.

- [ ] **Step 5: Build and run**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: PASS with the answer tests SKIPPING (unprovisioned — no ONNX model, no BEIR cache, no API key). **A skip is the correct outcome here and must be reported as a skip, not as a pass.**

- [ ] **Step 6: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs \
        tests/Rag.NET.Benchmarks.Quality.IntegrationTests/AnswerEngineArms.cs
git commit -m "test(arms): answer the engine arms through their engines, dense retrieval shared"
```

---

### Task 5: The three pilot gates

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–4.

- [ ] **Step 1: Gate 1 — context identity**

For every query, each engine arm's sources must be identical to the `dense` arm's: same chunk ids, same order. Because Step 1 of Task 4 made them share the retrieval case this should be true by construction — **assert it anyway**, because "by construction" is exactly the kind of claim that stops being true when someone edits a switch.

Collect the `dense` arm's chunk ids per query, and for each engine arm assert equality. `flare` is gated on its **initial** sources only — the lookahead's additions happen inside the engine and are the thing being measured.

Fail with a message naming the query, the arm, and the first differing rank.

- [ ] **Step 2: Gate 2 — call counts match the predicted shape**

`CachedGraphRagClient` exposes `Calls`. Snapshot it before and after each arm's answer for a query and assert the delta: `chatengine` exactly 1; `refine` exactly `ContextChunks`; `mapreduce` exactly `ContextChunks + 1`; FLARE arms ≥ 1 and ≤ 30.

**This only works if the arms answer sequentially for a given query**, and `AnswerAllAsync` runs queries under `Parallel.ForEachAsync`. Snapshot deltas across parallel work are meaningless. Either gate inside a sequential pilot-only path, or record per-arm call counts through a counting wrapper around the client rather than a global counter. **Choose the wrapper if in doubt — a global delta under parallelism is a silently wrong number, which is worse than no gate.**

- [ ] **Step 3: Gate 3 — lookahead observed firing in `flare`**

Assert that across the pilot's queries, the `flare` arm performed **at least one** lookahead retrieval.

This gate exists for a specific hazard: `SelfAssessmentConfidenceScorer` **fails open** — any error or unparsable output returns `1.0`, above the `0.6` threshold, so no lookahead fires. Under a refuse-on-miss replay, scorer misses would fail open and `flare` would silently become `flarefixed` while still reporting as `flare`. Without this gate, `flare − flarefixed ≈ 0` cannot distinguish "lookahead does nothing" from "lookahead never ran".

Implement by counting calls on the retriever handed to `flare` (wrap it), not by inspecting FLARE's internals.

- [ ] **Step 4: Emit the pilot's cost counters**

Extend the run's output to print, per arm: total calls, calls-per-query, input tokens, output tokens, tokens-per-query. `CachedGraphRagClient` already exposes `Calls`, `InputTokens`, `OutputTokens`.

**These counters are what price the full sweep** — the design's dollar table is derived, not measured, and is explicitly superseded by this output. For FLARE, the per-query call count is the number that settles the sweep's cost range.

- [ ] **Step 5: Build and run**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: PASS, answer tests SKIP. The gates cannot execute here; they are verified by reading. Say so plainly in the report.

- [ ] **Step 6: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirGraphRagAnswerTests.cs
git commit -m "test(arms): gate the engine pilot on context identity, call shape and lookahead"
```

---

### Task 6: Record the thread

**Files:**
- Modify: `docs/planning/ROADMAP.md`, `docs/planning/STATE.md`

- [ ] **Step 1: Record in Phase 6.2.1's block**

Strike the answer-engines item in the "Open threads" paragraph and add a paragraph recording: the five arms, the `chatengine` control and why it exists, the corrected cost (~$4 realistic, ~$21 worst case, dominated by FLARE's sentence count), the three gates, and — plainly — that **no pilot has been run**, because the machine that built this has no ONNX model, no BEIR cache and no API key.

**Do NOT mark Phase 6.2.1 complete.** It still owes HyDE, reranking, hybrid BM25, late chunking, SPLADE, every vector store through the SciFact parity leg, the second-corpus RAPTOR arm, and local search's unexplained yes/no abstention. The DoD's answer-engine clause is **not** met by arms alone — it needs the run.

- [ ] **Step 2: Record whether `flare` shipped**

State explicitly whether the `flare` arm is present or was excluded pending #414, per Task 4 Step 4.

- [ ] **Step 3: Update `STATE.md`**

Working State branch, Last completed, next step. **Note ROADMAP/STATE will conflict with #414's edits if that PR merges first** — a small, obvious textual conflict in Phase 6.2.1's block.

- [ ] **Step 4: Verify and commit**

```
dotnet test tests/Rag.NET.RepoConventions.Tests --no-build
git add docs/planning/ROADMAP.md docs/planning/STATE.md
git commit -m "chore(roadmap): record the answer-engine arms thread in phase 6.2.1"
git status
```

---

## Self-review

**Spec coverage.** Five arms → Task 1. Engines over the shared client, prompt untouched → Task 2. Throwing stub retriever → Task 2 + Task 3. Call-shape cost model verified without spending → Task 3. Dense retrieval shared, generation branched → Task 4. #414 dependency handled with an explicit fallback → Task 4 Step 4. Three gates → Task 5. Cost counters superseding the derived table → Task 5 Step 4. Roadmap without phase completion → Task 6. Every spec section has a task.

**Placeholder scan.** No TBD/TODO. Task 5's steps describe assertions rather than giving full code — deliberate, because the exact shape depends on whether Task 4 wired a counting wrapper or a global counter, and Step 2 states the decision rule (choose the wrapper if in doubt) rather than deferring it.

**Type consistency.** `AnswerEngineArms.Create(string, IChatClient, IRetriever?)` and `IsEngineArm(string)` are used identically in Tasks 3 and 4. `RagResponse.Answer` is the property read in Task 4 (verified against the record). `ContextChunks` is the existing harness constant in Task 4 and a local mirror in Task 3's isolated test.

**Known risk left in deliberately.** Task 5 Step 2's gate is the one most likely to be implemented wrongly, because a global call-count delta under `Parallel.ForEachAsync` looks right and is meaningless. The step names the trap and gives the safe default.
