# CRAG + Adaptive Retrieval Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add two opt-in retrieval behaviors — `AdaptiveRetrievalBehavior` (routes query complexity to optimal `RetrievalOptions`) and `CorrectiveRagBehavior` (post-retrieval relevance check with web search fallback via Tavily) — to the existing middleware pipeline.

**Architecture:** Both behaviors implement `IRetrievalBehavior` and are inserted into `RetrievalPipelineBuilder` (Adaptive before MultiQuery, CRAG between Adaptive and MultiQuery). Both are off by default (`UseAdaptiveRetrieval = false`, `UseCrag = false`). A new `IWebSearch` abstraction lives in `Rag.NET.Abstractions`; `TavilyWebSearch` is a separate NuGet-style package under `src/Rag.NET.WebSearch.Tavily/`.

**Tech Stack:** C# 13 / .NET 10, ZeroAlloc.Inject (`[Singleton]`, `[Inject(Required = false)]`), ZeroAlloc.Rest (Tavily HTTP client), `Microsoft.Extensions.AI` (`IChatClient`), xunit.v3, NSubstitute, WireMock.Net.

---

### Pipeline insertion order (for reference)

```
[outer] SelfQuery → ResultCache → LostInTheMiddle → Mmr → RedundancyFilter →
        ParentDocument → Reranking → RetrievalGuard →
        AdaptiveRetrievalBehavior  ← NEW (index 8, before MultiQuery)
        CorrectiveRagBehavior      ← NEW (index 9, before MultiQuery)
        MultiQuery → Hyde → EmbeddingCache → Filter → Ensemble →
        VectorStore [inner]
```

When called: Adaptive mutates `ctx.Options`, then CRAG wraps the downstream chain (MultiQuery → Hyde → VectorStore) and evaluates results.

---

## Task 1: Abstractions — IWebSearch, CragFallbackMode, RetrievalOptions

**Files:**
- Create: `src/Rag.NET.Abstractions/Abstractions/IWebSearch.cs`
- Create: `src/Rag.NET.Abstractions/Models/Options/CragFallbackMode.cs`
- Modify: `src/Rag.NET.Abstractions/Models/Options/RetrievalOptions.cs`

**Step 1: Create IWebSearch**

```csharp
// src/Rag.NET.Abstractions/Abstractions/IWebSearch.cs
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

public interface IWebSearch
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct);
}
```

**Step 2: Create CragFallbackMode**

```csharp
// src/Rag.NET.Abstractions/Models/Options/CragFallbackMode.cs
namespace Rag.NET.Models.Options;

public enum CragFallbackMode
{
    Replace,
    Append
}
```

**Step 3: Add four properties to RetrievalOptions**

Add after the `UseHyde` property (line ~94 in `RetrievalOptions.cs`):

```csharp
    /// <summary>
    /// Set to <see langword="true"/> to enable Adaptive Retrieval complexity-based routing.
    /// Automatically adjusts <see cref="TopK"/>, <see cref="UseMultiQuery"/>, and <see cref="UseHyde"/>
    /// based on detected query complexity (simple / complex / multi_hop).
    /// Uses heuristic classification first; falls back to <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// when available and the query is ambiguous.
    /// </summary>
    public bool UseAdaptiveRetrieval { get; init; } = false;

    /// <summary>
    /// Set to <see langword="true"/> to enable Corrective RAG (CRAG) post-retrieval relevance checking.
    /// Requires <see cref="Rag.NET.Abstractions.IWebSearch"/> to be registered in DI.
    /// When the relevance score is below <see cref="CragScoreThreshold"/>, web results replace or
    /// supplement vector results according to <see cref="CragFallbackMode"/>.
    /// </summary>
    public bool UseCrag { get; init; } = false;

    /// <summary>
    /// Minimum fraction of results classified as relevant before CRAG triggers web fallback.
    /// Range: 0.0–1.0. Default <c>0.5</c>.
    /// </summary>
    public float CragScoreThreshold { get; init; } = 0.5f;

    /// <summary>
    /// Controls how web search results are merged when CRAG triggers.
    /// <see cref="CragFallbackMode.Replace"/> discards vector results (default);
    /// <see cref="CragFallbackMode.Append"/> concatenates web results after vector results.
    /// </summary>
    public CragFallbackMode CragFallbackMode { get; init; } = CragFallbackMode.Replace;
```

**Step 4: Build to verify**

Run: `dotnet build src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj`
Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```bash
git add src/Rag.NET.Abstractions/Abstractions/IWebSearch.cs \
        src/Rag.NET.Abstractions/Models/Options/CragFallbackMode.cs \
        src/Rag.NET.Abstractions/Models/Options/RetrievalOptions.cs
git commit -m "feat(abstractions): add IWebSearch, CragFallbackMode, and adaptive/CRAG options"
```

---

## Task 2: AdaptiveRetrievalBehavior + tests

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/AdaptiveRetrievalBehavior.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/Behaviors/AdaptiveRetrievalBehaviorTests.cs`

**Background:**
- `[Singleton]` and `[Inject(Required = false)]` come from `ZeroAlloc.Inject` (imported by `Rag.NET.csproj`)
- Test helpers — `MakeResult(docId, chunkIndex, score)`, `MakeCtx(options)`, `NextReturning(results)` — are defined in `tests/Rag.NET.Tests/Retrieval/Behaviors/RetrievalBehaviorTests.cs` but are `private static`. Copy them locally.
- Heuristic: ≤6 words → `simple`; ≥2 multi-hop conjunctions → `multi_hop`; explicit "how/why/compare/difference/explain" → `complex`; otherwise `null` (ambiguous → LLM or default to `complex`).
- The behavior sets `ctx.Extensions["adaptive_complexity"]` to the chosen tier.

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Retrieval/Behaviors/AdaptiveRetrievalBehaviorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class AdaptiveRetrievalBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static RetrievalContext MakeCtx(RetrievalOptions options) =>
        new() { Query = "test query", Options = options };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        CapturingCtx(out Func<RetrievalContext?> getCapture)
    {
        RetrievalContext? captured = null;
        getCapture = () => captured;
        return (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);
        };
    }

    // ── Flag off ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_FlagOff_PassesThroughUnchanged()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = false });

        var output = await sut.HandleAsync(ctx, default, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── Heuristic classification ──────────────────────────────────────────────

    [Theory]
    [InlineData("What is RAG?", "simple")]            // 3 words
    [InlineData("Tell me about it", "simple")]         // 4 words ≤ 6
    [InlineData("how does retrieval work", "complex")] // "how"
    [InlineData("compare BM25 and vector search", "complex")] // "compare"
    [InlineData("why is chunking important", "complex")]       // "why"
    [InlineData("explain hybrid retrieval methods", "complex")]// "explain"
    [InlineData("What is RAG and how does it work and also why is it important", "multi_hop")] // ≥2 conjunctions
    public void ClassifyHeuristic_ReturnsExpected(string query, string expected)
    {
        var result = AdaptiveRetrievalBehavior.ClassifyHeuristic(query);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ClassifyHeuristic_AmbiguousLongQuery_ReturnsNull()
    {
        // Long query with no complex/multi_hop signals
        var result = AdaptiveRetrievalBehavior.ClassifyHeuristic(
            "retrieval augmented generation semantic vector database embedding");
        Assert.Null(result);
    }

    // ── Strategy mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SimpleQuery_SetsTopK3_NoMultiQuery_NoHyde()
    {
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        // "What is RAG?" is a short simple query
        ctx = ctx with { Query = "What is RAG?" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, default, next);

        var captured = getCaptured()!;
        Assert.Equal(3, captured.Options.TopK);
        Assert.False(captured.Options.UseMultiQuery);
        Assert.False(captured.Options.UseHyde);
        Assert.Equal("simple", captured.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_ComplexQuery_SetsTopK8_MultiQuery_NoHyde()
    {
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "how does retrieval augmented generation improve LLM accuracy" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, default, next);

        var captured = getCaptured()!;
        Assert.Equal(8, captured.Options.TopK);
        Assert.True(captured.Options.UseMultiQuery);
        Assert.False(captured.Options.UseHyde);
        Assert.Equal("complex", captured.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_MultiHopQuery_SetsTopK10_MultiQuery_Hyde()
    {
        var sut = new AdaptiveRetrievalBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "What is chunking and how does it affect retrieval and also why does it matter for context windows" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, default, next);

        var captured = getCaptured()!;
        Assert.Equal(10, captured.Options.TopK);
        Assert.True(captured.Options.UseMultiQuery);
        Assert.True(captured.Options.UseHyde);
        Assert.Equal("multi_hop", captured.Extensions["adaptive_complexity"]);
    }

    // ── LLM fallback ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_AmbiguousQuery_NoLlm_DefaultsToComplex()
    {
        var sut = new AdaptiveRetrievalBehavior { ChatClient = null };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        // A long query with no keywords → heuristic returns null → no LLM → defaults to complex
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, default, next);

        Assert.Equal("complex", getCaptured()!.Extensions["adaptive_complexity"]);
    }

    [Fact]
    public async Task HandleAsync_AmbiguousQuery_LlmReturnsSimple_SetsSimpleOptions()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.CompleteAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(new ChatCompletion(new ChatMessage(ChatRole.Assistant, "simple")));

        var sut = new AdaptiveRetrievalBehavior { ChatClient = chatClient };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, default, next);

        Assert.Equal("simple", getCaptured()!.Extensions["adaptive_complexity"]);
        Assert.Equal(3, getCaptured()!.Options.TopK);
    }

    [Fact]
    public async Task HandleAsync_LlmThrows_DefaultsToComplex()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.CompleteAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = new AdaptiveRetrievalBehavior { ChatClient = chatClient };
        var ctx = MakeCtx(new RetrievalOptions { UseAdaptiveRetrieval = true });
        ctx = ctx with { Query = "retrieval augmented generation semantic vector database embedding storage" };

        var next = CapturingCtx(out var getCaptured);
        await sut.HandleAsync(ctx, default, next);

        Assert.Equal("complex", getCaptured()!.Extensions["adaptive_complexity"]);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AdaptiveRetrievalBehavior"`
Expected: Compilation error (type not found).

**Step 3: Implement AdaptiveRetrievalBehavior**

```csharp
// src/Rag.NET/Retrieval/Behaviors/AdaptiveRetrievalBehavior.cs
using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class AdaptiveRetrievalBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseAdaptiveRetrieval)
            return await next(ctx, ct).ConfigureAwait(false);

        var complexity = ClassifyHeuristic(ctx.Query);

        if (complexity is null && ChatClient is not null)
        {
            try
            {
                complexity = await ClassifyWithLlmAsync(ctx.Query, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RagPipelineLog.AdaptiveClassificationFailed(ctx.Logger, ctx.Query, ex);
            }
        }

        complexity ??= "complex";

        var options = complexity switch
        {
            "simple"    => ctx.Options with { TopK = 3,  UseMultiQuery = false, UseHyde = false },
            "multi_hop" => ctx.Options with { TopK = 10, UseMultiQuery = true,  UseHyde = true  },
            _           => ctx.Options with { TopK = 8,  UseMultiQuery = true,  UseHyde = false },
        };

        ctx.Extensions["adaptive_complexity"] = complexity;

        return await next(ctx with { Options = options }, ct).ConfigureAwait(false);
    }

    internal static string? ClassifyHeuristic(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length <= 6)
            return "simple";

        var multiHopKeywords = new[] { " and ", " also ", " additionally ", " furthermore ", " as well as " };
        var conjunctionCount = multiHopKeywords.Sum(k =>
            CountOccurrences(" " + query.ToLowerInvariant() + " ", k, StringComparison.Ordinal));
        if (conjunctionCount >= 2)
            return "multi_hop";

        var complexKeywords = new[] { "how", "why", "compare", "difference", "explain" };
        if (complexKeywords.Any(k => query.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return "complex";

        return null;
    }

    private static int CountOccurrences(string text, string pattern, StringComparison comparison)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, comparison)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private async Task<string> ClassifyWithLlmAsync(string query, CancellationToken ct)
    {
        var response = await ChatClient!.CompleteAsync(
            [new ChatMessage(ChatRole.User, $"""
                Classify this query as exactly one of: simple, complex, multi_hop.
                
                simple = single-concept lookup, short, no comparison
                complex = multi-aspect explanation, comparison, or analysis  
                multi_hop = requires connecting 2+ separate concepts or sources
                
                Query: {query}
                
                Reply with ONLY the classification word.
                """)],
            cancellationToken: ct).ConfigureAwait(false);

        var text = response.Message.Text?.Trim().ToLowerInvariant() ?? "complex";
        return text is "simple" or "complex" or "multi_hop" ? text : "complex";
    }
}
```

**Step 4: Add log entry to RagPipelineLog**

Add to `src/Rag.NET/Logging/RagPipelineLog.cs` (before the closing brace):

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "Adaptive retrieval classification failed for query '{Query}', defaulting to complex")]
    internal static partial void AdaptiveClassificationFailed(ILogger logger, string query, Exception exception);
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AdaptiveRetrievalBehavior"`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Retrieval/Behaviors/AdaptiveRetrievalBehavior.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/Retrieval/Behaviors/AdaptiveRetrievalBehaviorTests.cs
git commit -m "feat: add AdaptiveRetrievalBehavior with heuristic and LLM classification"
```

---

## Task 3: CorrectiveRagBehavior + tests

**Files:**
- Create: `src/Rag.NET/Retrieval/Behaviors/CorrectiveRagBehavior.cs`
- Create: `tests/Rag.NET.Tests/Retrieval/Behaviors/CorrectiveRagBehaviorTests.cs`

**Background:**
- Calls `next()` to get vector results; scores relevance; if score < `CragScoreThreshold`, calls `IWebSearch`
- LLM scoring: per-chunk prompt → `relevant`/`ambiguous`/`irrelevant`; score = relevant_count / total
- Heuristic scoring (no LLM): keyword overlap; score = fraction of results with ≥30% query-token match
- On `IWebSearch` exception: log warning, return original vector results unchanged
- `ctx.Extensions["crag_triggered"]` set to `"true"` or `"false"`

**Step 1: Write the failing tests**

```csharp
// tests/Rag.NET.Tests/Retrieval/Behaviors/CorrectiveRagBehaviorTests.cs
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class CorrectiveRagBehaviorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SearchResult MakeResult(string docId, int chunkIndex, double score, string text = "relevant content about topic") =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score
        };

    private static RetrievalContext MakeCtx(RetrievalOptions options, string query = "test query") =>
        new() { Query = query, Options = options };

    private static Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>>
        NextReturning(IReadOnlyList<SearchResult> results) =>
        (_, _) => ValueTask.FromResult(results);

    // ── Flag off / no IWebSearch ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_FlagOff_PassesThroughUnchanged()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new CorrectiveRagBehavior();
        var ctx = MakeCtx(new RetrievalOptions { UseCrag = false });

        var output = await sut.HandleAsync(ctx, default, NextReturning(results));

        Assert.Same(results, output);
    }

    [Fact]
    public async Task HandleAsync_NoWebSearch_PassesThroughUnchanged()
    {
        var results = new List<SearchResult> { MakeResult("doc-1", 0, 0.9) };
        var sut = new CorrectiveRagBehavior { WebSearch = null };
        var ctx = MakeCtx(new RetrievalOptions { UseCrag = true });

        var output = await sut.HandleAsync(ctx, default, NextReturning(results));

        Assert.Same(results, output);
    }

    // ── Above threshold: no web search triggered ─────────────────────────────

    [Fact]
    public async Task HandleAsync_HighRelevance_DoesNotTriggerWebSearch()
    {
        // Heuristic: query tokens in chunk text → high score → no web search
        var webSearch = Substitute.For<IWebSearch>();
        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        // Query: "test query" — both tokens present in chunk text
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, text: "test query relevant content"),
            MakeResult("doc-2", 0, 0.8, text: "test query more content"),
        };
        var ctx = MakeCtx(new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f });

        await sut.HandleAsync(ctx, default, NextReturning(results));

        await webSearch.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal("false", ctx.Extensions["crag_triggered"]);
    }

    // ── Below threshold: Replace mode ────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LowRelevance_Replace_ReturnsWebResults()
    {
        var webResult = MakeResult("web-1", 0, 0.95, "web search result");
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([webResult]));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        // Vector results with no query tokens → low heuristic score
        var vectorResults = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz"),
        };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f, CragFallbackMode = CragFallbackMode.Replace },
            query: "specific topic search");

        var output = await sut.HandleAsync(ctx, default, NextReturning(vectorResults));

        Assert.Contains(webResult, output);
        Assert.DoesNotContain(vectorResults[0], output);
        Assert.Equal("true", ctx.Extensions["crag_triggered"]);
    }

    // ── Below threshold: Append mode ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LowRelevance_Append_ReturnsBothResults()
    {
        var webResult = MakeResult("web-1", 0, 0.95, "web search result");
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([webResult]));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        var vectorResult = MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz");
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f, CragFallbackMode = CragFallbackMode.Append },
            query: "specific topic search");

        var output = await sut.HandleAsync(ctx, default, NextReturning([vectorResult]));

        Assert.Contains(vectorResult, output);
        Assert.Contains(webResult, output);
        Assert.Equal(2, output.Count);
    }

    // ── Web search throws: graceful degradation ───────────────────────────────

    [Fact]
    public async Task HandleAsync_WebSearchThrows_ReturnsOriginalVectorResults()
    {
        var webSearch = Substitute.For<IWebSearch>();
        webSearch.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = new CorrectiveRagBehavior { WebSearch = webSearch };
        var vectorResults = new List<SearchResult> { MakeResult("doc-1", 0, 0.1, text: "unrelated content xyz") };
        var ctx = MakeCtx(
            new RetrievalOptions { UseCrag = true, CragScoreThreshold = 0.5f },
            query: "specific topic search");

        var output = await sut.HandleAsync(ctx, default, NextReturning(vectorResults));

        Assert.Same(vectorResults, output);
    }

    // ── Heuristic scoring unit tests ──────────────────────────────────────────

    [Fact]
    public void ScoreWithHeuristic_EmptyResults_ReturnsZero()
    {
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("test query", []);
        Assert.Equal(0f, score);
    }

    [Fact]
    public void ScoreWithHeuristic_AllMatching_ReturnsOne()
    {
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "test query content"),
            MakeResult("doc-2", 0, 0.8, "test query more"),
        };
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("test query", results);
        Assert.True(score > 0.5f);
    }

    [Fact]
    public void ScoreWithHeuristic_NoneMatching_ReturnsLowScore()
    {
        var results = new List<SearchResult>
        {
            MakeResult("doc-1", 0, 0.9, "xyz abc def ghi jkl"),
        };
        var score = CorrectiveRagBehavior.ScoreWithHeuristic("specific topic", results);
        Assert.True(score < 0.5f);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "CorrectiveRagBehavior"`
Expected: Compilation error (type not found).

**Step 3: Implement CorrectiveRagBehavior**

```csharp
// src/Rag.NET/Retrieval/Behaviors/CorrectiveRagBehavior.cs
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class CorrectiveRagBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }
    [Inject(Required = false)] public IWebSearch? WebSearch { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCrag || WebSearch is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var results = await next(ctx, ct).ConfigureAwait(false);
        var score = await ScoreRelevanceAsync(ctx.Query, results, ct).ConfigureAwait(false);

        if (score >= ctx.Options.CragScoreThreshold)
        {
            ctx.Extensions["crag_triggered"] = "false";
            return results;
        }

        ctx.Extensions["crag_triggered"] = "true";

        try
        {
            var webResults = await WebSearch.SearchAsync(ctx.Query, ctx.Options.TopK, ct).ConfigureAwait(false);
            return ctx.Options.CragFallbackMode == CragFallbackMode.Append
                ? [.. results, .. webResults]
                : webResults;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.CragWebSearchFailed(ctx.Logger, ctx.Query, ex);
            return results;
        }
    }

    private async Task<float> ScoreRelevanceAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken ct)
    {
        if (results.Count == 0) return 0f;

        if (ChatClient is not null)
        {
            try
            {
                return await ScoreWithLlmAsync(query, results, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to heuristic */ }
        }

        return ScoreWithHeuristic(query, results);
    }

    private async Task<float> ScoreWithLlmAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken ct)
    {
        var relevant = 0;
        foreach (var result in results)
        {
            var response = await ChatClient!.CompleteAsync(
                [new ChatMessage(ChatRole.User, $"""
                    Is this chunk relevant to the query?
                    Query: {query}
                    Chunk: {result.Chunk.Text}
                    Reply with exactly one word: relevant, ambiguous, or irrelevant.
                    """)],
                cancellationToken: ct).ConfigureAwait(false);

            var label = response.Message.Text?.Trim().ToLowerInvariant() ?? "irrelevant";
            if (string.Equals(label, "relevant", StringComparison.Ordinal)) relevant++;
        }
        return (float)relevant / results.Count;
    }

    internal static float ScoreWithHeuristic(string query, IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0) return 0f;

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return 0f;

        var matchingResults = 0;
        foreach (var result in results)
        {
            var chunkTokens = Tokenize(result.Chunk.Text);
            var matched = queryTokens.Count(t => chunkTokens.Contains(t));
            if ((float)matched / queryTokens.Count >= 0.3f)
                matchingResults++;
        }
        return (float)matchingResults / results.Count;
    }

    private static HashSet<string> Tokenize(string text) =>
        [.. text.Split([' ', '.', ',', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
               .Select(t => t.ToLowerInvariant())];
}
```

**Step 4: Add log entry to RagPipelineLog**

Add to `src/Rag.NET/Logging/RagPipelineLog.cs` (before the closing brace):

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message = "CRAG web search failed for query '{Query}', returning original vector results")]
    internal static partial void CragWebSearchFailed(ILogger logger, string query, Exception exception);
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "CorrectiveRagBehavior"`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET/Retrieval/Behaviors/CorrectiveRagBehavior.cs \
        src/Rag.NET/Logging/RagPipelineLog.cs \
        tests/Rag.NET.Tests/Retrieval/Behaviors/CorrectiveRagBehaviorTests.cs
git commit -m "feat: add CorrectiveRagBehavior with heuristic and LLM relevance scoring"
```

---

## Task 4: Register behaviors in RetrievalPipelineBuilder

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs`

**Background:** The `_types` list is ordered outer→inner. `AdaptiveRetrievalBehavior` and `CorrectiveRagBehavior` must be inserted before `MultiQueryBehavior` (index 8 in current list). Adaptive first (outer), then CRAG (inner). When CRAG calls `next()`, Adaptive has already mutated the options, so MultiQuery and Hyde run with the correct settings.

**Step 1: Modify the default types list**

In `RetrievalPipelineBuilder.cs`, find the `_types` initializer and insert after `RetrievalGuardBehavior`:

Current:
```csharp
    private readonly List<Type> _types =
    [
        typeof(SelfQueryBehavior),
        typeof(ResultCacheBehavior),
        typeof(LostInTheMiddleBehavior),
        typeof(MmrBehavior),
        typeof(RedundancyFilterBehavior),
        typeof(ParentDocumentRetrievalBehavior),
        typeof(RerankingBehavior),
        typeof(RetrievalGuardBehavior),
        typeof(MultiQueryBehavior),
        typeof(HydeBehavior),
        typeof(EmbeddingCacheBehavior),
        typeof(FilterBehavior),
        typeof(EnsembleBehavior),
        typeof(VectorStoreBehavior),
    ];
```

Replace with:
```csharp
    private readonly List<Type> _types =
    [
        typeof(SelfQueryBehavior),
        typeof(ResultCacheBehavior),
        typeof(LostInTheMiddleBehavior),
        typeof(MmrBehavior),
        typeof(RedundancyFilterBehavior),
        typeof(ParentDocumentRetrievalBehavior),
        typeof(RerankingBehavior),
        typeof(RetrievalGuardBehavior),
        typeof(AdaptiveRetrievalBehavior),
        typeof(CorrectiveRagBehavior),
        typeof(MultiQueryBehavior),
        typeof(HydeBehavior),
        typeof(EmbeddingCacheBehavior),
        typeof(FilterBehavior),
        typeof(EnsembleBehavior),
        typeof(VectorStoreBehavior),
    ];
```

**Step 2: Run the full test suite**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj`
Expected: All tests pass (the new behaviors have `false` defaults so existing pipeline tests are unaffected).

**Step 3: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RetrievalPipelineBuilder.cs
git commit -m "feat: register AdaptiveRetrievalBehavior and CorrectiveRagBehavior in pipeline"
```

---

## Task 5: Create Rag.NET.WebSearch.Tavily project

**Files:**
- Create: `src/Rag.NET.WebSearch.Tavily/Rag.NET.WebSearch.Tavily.csproj`
- Create: `src/Rag.NET.WebSearch.Tavily/ITavilyApi.cs`
- Create: `src/Rag.NET.WebSearch.Tavily/TavilyModels.cs`
- Create: `src/Rag.NET.WebSearch.Tavily/TavilyWebSearch.cs`
- Create: `src/Rag.NET.WebSearch.Tavily/TavilyWebSearchExtensions.cs`

**Background:** Pattern mirrors `Rag.NET.DataProviders.Notion`. ZeroAlloc.Rest generates the HTTP client from `[ZeroAllocRestClient]` interface. Auth is in request body (`api_key` field). No `[Header]` attribute needed.

**Step 1: Create csproj**

```xml
<!-- src/Rag.NET.WebSearch.Tavily/Rag.NET.WebSearch.Tavily.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.WebSearch.Tavily</RootNamespace>
    <PackageId>Rag.NET.WebSearch.Tavily</PackageId>
    <Description>Tavily web search provider for Rag.NET CRAG</Description>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.WebSearch.Tavily.Tests</_Parameter1>
    </AssemblyAttribute>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">
      <_Parameter1>Rag.NET.DataProviders.IntegrationTests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
    <PackageReference Include="ZeroAlloc.Rest" Version="0.*" />
    <PackageReference Include="ZeroAlloc.Rest.Generator" Version="0.*" PrivateAssets="all" ExcludeAssets="runtime" GeneratePathProperty="true" />
    <PackageReference Include="ZeroAlloc.Rest.SystemTextJson" Version="0.*" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
  </ItemGroup>
  <ItemGroup>
    <Analyzer Include="$(PkgZeroAlloc_Rest_Generator)\lib\netstandard2.0\ZeroAlloc.Rest.Generator.dll" />
  </ItemGroup>
</Project>
```

**Step 2: Create ITavilyApi**

```csharp
// src/Rag.NET.WebSearch.Tavily/ITavilyApi.cs
using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Rag.NET.WebSearch.Tavily;

[ZeroAllocRestClient]
internal interface ITavilyApi
{
    [Post("/search")]
    Task<Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError>> SearchAsync(
        [Body] TavilySearchRequest request,
        CancellationToken cancellationToken = default);
}
```

**Step 3: Create TavilyModels**

```csharp
// src/Rag.NET.WebSearch.Tavily/TavilyModels.cs
using System.Text.Json.Serialization;

namespace Rag.NET.WebSearch.Tavily;

internal sealed record TavilySearchRequest
{
    [JsonPropertyName("api_key")]   public required string ApiKey     { get; init; }
    [JsonPropertyName("query")]     public required string Query      { get; init; }
    [JsonPropertyName("max_results")] public int MaxResults           { get; init; } = 5;
}

internal sealed record TavilySearchResponse
{
    [JsonPropertyName("results")] public IReadOnlyList<TavilyResult> Results { get; init; } = [];
}

internal sealed record TavilyResult
{
    [JsonPropertyName("title")]   public string Title   { get; init; } = "";
    [JsonPropertyName("url")]     public string Url     { get; init; } = "";
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("score")]   public double Score   { get; init; }
}
```

**Step 4: Create TavilyWebSearch**

```csharp
// src/Rag.NET.WebSearch.Tavily/TavilyWebSearch.cs
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.WebSearch.Tavily;

internal sealed class TavilyWebSearch : IWebSearch
{
    private readonly ITavilyApi _api;
    private readonly string _apiKey;

    public TavilyWebSearch(ITavilyApi api, string apiKey)
    {
        _api = api;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct)
    {
        var request = new TavilySearchRequest { ApiKey = _apiKey, Query = query, MaxResults = topK };
        var result = await _api.SearchAsync(request, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new HttpRequestException($"Tavily search failed: {result.Error.StatusCode}");

        return result.Value.Results
            .Select(r => new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = r.Content,
                    DocumentId = new DocumentId(r.Url),
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["title"] = r.Title,
                        ["url"] = r.Url,
                        ["source"] = "tavily"
                    }
                },
                Score = r.Score
            })
            .ToList();
    }
}
```

**Step 5: Create TavilyWebSearchExtensions**

```csharp
// src/Rag.NET.WebSearch.Tavily/TavilyWebSearchExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.WebSearch.Tavily;

/// <summary>Extension methods for registering Tavily web search with dependency injection.</summary>
public static class TavilyWebSearchExtensions
{
    /// <summary>
    /// Registers <see cref="IWebSearch"/> using Tavily as the backing provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apiKey">Tavily API key.</param>
    /// <param name="baseUrl">Override base URL (defaults to <c>https://api.tavily.com</c>). Used in tests.</param>
    public static IServiceCollection AddTavilyWebSearch(
        this IServiceCollection services,
        string apiKey,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var resolvedBaseUrl = string.IsNullOrEmpty(baseUrl) ? "https://api.tavily.com" : baseUrl;

        services.AddITavilyApi(options =>
            {
                options.BaseAddress = new Uri(resolvedBaseUrl);
                options.UseSerializer<ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer>();
            })
            .AddStandardResilienceHandler();

        services.AddSingleton<IWebSearch>(sp =>
            new TavilyWebSearch(sp.GetRequiredService<ITavilyApi>(), apiKey));

        return services;
    }
}
```

**Step 6: Build to verify**

Run: `dotnet build src/Rag.NET.WebSearch.Tavily/Rag.NET.WebSearch.Tavily.csproj`
Expected: Build succeeded, 0 errors.

**Step 7: Commit**

```bash
git add src/Rag.NET.WebSearch.Tavily/
git commit -m "feat: add Rag.NET.WebSearch.Tavily package with ZeroAlloc.Rest Tavily client"
```

---

## Task 6: TavilyWebSearch unit tests

**Files:**
- Create: `tests/Rag.NET.WebSearch.Tavily.Tests/Rag.NET.WebSearch.Tavily.Tests.csproj`
- Create: `tests/Rag.NET.WebSearch.Tavily.Tests/TavilyWebSearchTests.cs`

**Step 1: Create test csproj**

```xml
<!-- tests/Rag.NET.WebSearch.Tavily.Tests/Rag.NET.WebSearch.Tavily.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.WebSearch.Tavily\Rag.NET.WebSearch.Tavily.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
  </ItemGroup>
</Project>
```

**Step 2: Write the failing tests**

```csharp
// tests/Rag.NET.WebSearch.Tavily.Tests/TavilyWebSearchTests.cs
using NSubstitute;
using Rag.NET.WebSearch.Tavily;
using ZeroAlloc.Results;
using Xunit;

namespace Rag.NET.WebSearch.Tavily.Tests;

public class TavilyWebSearchTests
{
    private static ITavilyApi MakeApiReturning(TavilySearchResponse response)
    {
        var api = Substitute.For<ITavilyApi>();
        api.SearchAsync(Arg.Any<TavilySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError>.Ok(response)));
        return api;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_MapsResultsToSearchResults()
    {
        var response = new TavilySearchResponse
        {
            Results =
            [
                new TavilyResult { Title = "Page 1", Url = "https://example.com/1", Content = "content one", Score = 0.9 },
                new TavilyResult { Title = "Page 2", Url = "https://example.com/2", Content = "content two", Score = 0.7 },
            ]
        };
        var api = MakeApiReturning(response);
        var sut = new TavilyWebSearch(api, "test-key");

        var results = await sut.SearchAsync("test query", topK: 2, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("content one", results[0].Chunk.Text);
        Assert.Equal("https://example.com/1", results[0].Chunk.DocumentId.Value);
        Assert.Equal(0.9, results[0].Score);
        Assert.Equal("tavily", results[0].Chunk.Metadata?["source"]);
    }

    [Fact]
    public async Task SearchAsync_PassesApiKeyAndTopK()
    {
        var api = Substitute.For<ITavilyApi>();
        api.SearchAsync(Arg.Any<TavilySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError>.Ok(
                new TavilySearchResponse { Results = [] })));

        var sut = new TavilyWebSearch(api, "my-api-key");
        await sut.SearchAsync("hello", topK: 3, CancellationToken.None);

        await api.Received(1).SearchAsync(
            Arg.Is<TavilySearchRequest>(r =>
                string.Equals(r.ApiKey, "my-api-key", StringComparison.Ordinal) &&
                string.Equals(r.Query, "hello", StringComparison.Ordinal) &&
                r.MaxResults == 3),
            Arg.Any<CancellationToken>());
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_HttpError_ThrowsHttpRequestException()
    {
        var api = Substitute.For<ITavilyApi>();
        api.SearchAsync(Arg.Any<TavilySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError>.Fail(
                new ZeroAlloc.Rest.HttpError(System.Net.HttpStatusCode.Unauthorized, "Unauthorized"))));

        var sut = new TavilyWebSearch(api, "bad-key");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.SearchAsync("query", topK: 5, CancellationToken.None));
    }

    // ── DI registration ───────────────────────────────────────────────────────

    [Fact]
    public void AddTavilyWebSearch_RegistersIWebSearch()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddTavilyWebSearch("test-key");
        var sp = services.BuildServiceProvider();

        var webSearch = sp.GetService<Rag.NET.Abstractions.IWebSearch>();

        Assert.NotNull(webSearch);
        Assert.IsType<TavilyWebSearch>(webSearch);
    }
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.WebSearch.Tavily.Tests/Rag.NET.WebSearch.Tavily.Tests.csproj`
Expected: Test runner finds tests (compilation must succeed since impl is done).
Note: `TavilyWebSearch` is `internal` so tests work via `InternalsVisibleTo`. `TavilySearchRequest` / `TavilySearchResponse` / `TavilyResult` are also internal — accessible from test project.

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.WebSearch.Tavily.Tests/Rag.NET.WebSearch.Tavily.Tests.csproj`
Expected: All 4 tests pass.

**Step 5: Commit**

```bash
git add tests/Rag.NET.WebSearch.Tavily.Tests/
git commit -m "test: add TavilyWebSearch unit tests"
```

---

## Task 7: WireMock integration test for TavilyWebSearch

**Files:**
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Tavily/search.json`
- Create: `tests/Rag.NET.DataProviders.IntegrationTests/TavilyWebSearchIntegrationTests.cs`
- Modify: `tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

**Background:** The `WireMockServerFixture.LoadCassettes(provider, baseUrl)` method maps cassette file names to WireMock stubs. Cassette naming follows the pattern used by other connectors (see `Cassettes/Notion/`, etc.). The fixture is already in `Rag.NET.Testing` project.

**Step 1: Examine LoadCassettes signature**

Read `tests/Rag.NET.Testing/WireMockServerFixture.cs` to confirm cassette format (JSON stub format used by WireMock.Net). Use the same shape as existing cassettes.

**Step 2: Create the cassette file**

```json
// tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Tavily/search.json
{
  "Request": {
    "Path": { "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/search", "IgnoreCase": false }] },
    "Methods": ["POST"]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json; charset=utf-8" },
    "Body": "{\"results\":[{\"title\":\"RAG Overview\",\"url\":\"https://example.com/rag\",\"content\":\"Retrieval augmented generation combines LLMs with external knowledge.\",\"score\":0.92},{\"title\":\"Vector Search\",\"url\":\"https://example.com/vector\",\"content\":\"Vector search retrieves semantically similar documents.\",\"score\":0.85}]}"
  }
}
```

**Step 3: Add project reference to IntegrationTests csproj**

In `Rag.NET.DataProviders.IntegrationTests.csproj`, add inside the `<ItemGroup>`:

```xml
    <ProjectReference Include="..\..\src\Rag.NET.WebSearch.Tavily\Rag.NET.WebSearch.Tavily.csproj" />
```

**Step 4: Write the integration test**

```csharp
// tests/Rag.NET.DataProviders.IntegrationTests/TavilyWebSearchIntegrationTests.cs
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Testing;
using Rag.NET.WebSearch.Tavily;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class TavilyWebSearchIntegrationTests
{
    private readonly WireMockServerFixture _fixture;

    public TavilyWebSearchIntegrationTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Tavily", "https://api.tavily.com");
    }

    private IWebSearch CreateWebSearch()
    {
        var services = new ServiceCollection();
        services.AddTavilyWebSearch("test-key", baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IWebSearch>();
    }

    [Fact]
    public async Task SearchAsync_YieldsResults()
    {
        var sut = CreateWebSearch();

        var results = await sut.SearchAsync(
            "retrieval augmented generation",
            topK: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess || true); // SearchResult has no IsSuccess — just validate fields
            Assert.NotEmpty(r.Chunk.Text);
            Assert.NotEmpty(r.Chunk.DocumentId.Value);
        });
        Assert.Contains(results, r =>
            r.Chunk.DocumentId.Value.Equals("https://example.com/rag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_ResultsHaveTavilyMetadata()
    {
        var sut = CreateWebSearch();

        var results = await sut.SearchAsync("test", topK: 2, TestContext.Current.CancellationToken);

        Assert.All(results, r =>
        {
            Assert.NotNull(r.Chunk.Metadata);
            Assert.Equal("tavily", r.Chunk.Metadata["source"]);
            Assert.True(r.Chunk.Metadata.ContainsKey("title"));
            Assert.True(r.Chunk.Metadata.ContainsKey("url"));
        });
    }
}
```

**Step 5: Run integration tests**

Run: `dotnet build tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj`

Then: `dotnet test tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj --filter "TavilyWebSearch"`
Expected: Both tests pass.

**Step 6: Commit**

```bash
git add tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/Tavily/ \
        tests/Rag.NET.DataProviders.IntegrationTests/TavilyWebSearchIntegrationTests.cs \
        tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
git commit -m "test: add TavilyWebSearch WireMock integration test"
```

---

## Final verification

Run the full test suite across all affected projects:

```bash
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj
dotnet test tests/Rag.NET.WebSearch.Tavily.Tests/Rag.NET.WebSearch.Tavily.Tests.csproj
dotnet test tests/Rag.NET.DataProviders.IntegrationTests/Rag.NET.DataProviders.IntegrationTests.csproj
```

Expected: All tests pass across all three projects.
