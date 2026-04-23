# Contextual Compression Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Post-retrieval chunk compression behind an `IContextualCompressor` abstraction with extractive (embedding similarity) and abstractive (parallel per-chunk LLM call) strategies.

**Architecture:** Compression is non-destructive — a new `SearchResult.CompressedText` property holds the compressed view; `Chunk.Text` is never mutated. `ChatAnswerEngine` invokes the compressor by default before prompt building; an opt-in `ContextualCompressionRetrievalBehavior` exposes the same compressor to the retrieval pipeline. See design doc: `docs/plans/2026-04-22-contextual-compression-design.md`.

**Tech Stack:** C# 13 / .NET 10, Microsoft.Extensions.AI (`IEmbeddingGenerator<string, Embedding<float>>`, `IChatClient`), xUnit v3 + NSubstitute, `Microsoft.ML.Tokenizers` (cl100k_base), ZeroAlloc.Inject (DI codegen — not strictly required for this feature but used elsewhere).

**Conventions (non-obvious — read this before writing code):**
- All projects target `net10.0` with `TreatWarningsAsErrors=true`. Zero warnings is mandatory.
- Use `[LoggerMessage]` source-generated logs (Rag.NET pattern). See `src/Rag.NET.Security/RegexChunkSanitiser.cs` for a reference.
- Use `ConfigureAwait(false)` on every `await` (MA0004 enforced).
- Use explicit `foreach` loops, not LINQ `.Any()` inside hot paths (ZA0601 analyzer treats LINQ-in-loop as an error).
- NSubstitute cannot `.ThrowsAsync` on `ValueTask`-returning methods. Use `.Returns(_ => new ValueTask<T>(Task.FromException<T>(ex)))` with `#pragma warning disable EPS06` / `restore EPS06`.
- Test files use `TestContext.Current.CancellationToken` for cancellation token parameter.
- **Each task MUST end with `dotnet build -c Release` succeeding with 0 warnings / 0 errors.**

---

## Task 1: Add `CompressedText` to `SearchResult`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/SearchResult.cs`
- Test: `tests/Rag.NET.Tests/Models/SearchResultTests.cs` (create if missing)

**Step 1: Write the failing test**

Create or append to `tests/Rag.NET.Tests/Models/SearchResultTests.cs`:

```csharp
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class SearchResultCompressedTextTests
{
    [Fact]
    public void CompressedText_DefaultsToNull()
    {
        var sut = new SearchResult
        {
            Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("d"), ChunkIndex = 0 },
            Score = 0.5,
        };

        Assert.Null(sut.CompressedText);
    }

    [Fact]
    public void CompressedText_CanBeSetViaInit()
    {
        var sut = new SearchResult
        {
            Chunk = new TextChunk { Text = "hello world", DocumentId = new DocumentId("d"), ChunkIndex = 0 },
            Score = 0.5,
            CompressedText = "hello",
        };

        Assert.Equal("hello", sut.CompressedText);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~SearchResultCompressedTextTests" -v m`

Expected: FAIL — compile error, `SearchResult` has no `CompressedText` property.

**Step 3: Modify `SearchResult`**

Replace `src/Rag.NET.Abstractions/Models/SearchResult.cs` contents with:

```csharp
namespace Rag.NET.Models;

public sealed record SearchResult
{
    public required TextChunk Chunk { get; init; }
    public required double Score { get; init; }

    /// <summary>
    /// Compressed-for-LLM view of <see cref="TextChunk.Text"/>. <see langword="null"/>
    /// when no compression was applied. Answer engines prefer this over
    /// <see cref="TextChunk.Text"/> when non-null.
    /// </summary>
    public string? CompressedText { get; init; }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~SearchResultCompressedTextTests" -v m`

Expected: PASS — both tests green.

**Step 5: Verify no regressions**

Run: `dotnet build src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj -c Release`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

**Step 6: Commit**

```bash
git add src/Rag.NET.Abstractions/Models/SearchResult.cs tests/Rag.NET.Tests/Models/SearchResultTests.cs
git commit -m "feat(abstractions): add SearchResult.CompressedText for non-destructive compression output"
```

---

## Task 2: Add `SkipCompression` to `RagOptions`

**Files:**
- Modify: `src/Rag.NET.Abstractions/Models/Options/RagOptions.cs`
- Test: `tests/Rag.NET.Tests/Models/RagOptionsTests.cs` (create/append)

**Step 1: Write the failing test**

Create or append to `tests/Rag.NET.Tests/Models/RagOptionsTests.cs`:

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Models;

public class RagOptionsSkipCompressionTests
{
    [Fact]
    public void SkipCompression_DefaultsToFalse()
    {
        var sut = new RagOptions();
        Assert.False(sut.SkipCompression);
    }

    [Fact]
    public void SkipCompression_CanBeSet()
    {
        var sut = new RagOptions { SkipCompression = true };
        Assert.True(sut.SkipCompression);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~RagOptionsSkipCompressionTests" -v m`

Expected: FAIL — compile error.

**Step 3: Modify `RagOptions`**

In `src/Rag.NET.Abstractions/Models/Options/RagOptions.cs`, add this property at the end of the class:

```csharp
    /// <summary>
    /// Bypass contextual compression for this call even when an
    /// <c>IContextualCompressor</c> is registered. Use when raw source
    /// text is required (admin tooling, UI citation rendering).
    /// </summary>
    public bool SkipCompression { get; set; }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~RagOptionsSkipCompressionTests" -v m`

Expected: PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET.Abstractions/Models/Options/RagOptions.cs tests/Rag.NET.Tests/Models/RagOptionsTests.cs
git commit -m "feat(abstractions): add RagOptions.SkipCompression opt-out flag"
```

---

## Task 3: `IContextualCompressor` + options + enum (abstraction only)

**Files:**
- Create: `src/Rag.NET.QueryTechniques/ContextualCompression/IContextualCompressor.cs`
- Create: `src/Rag.NET.QueryTechniques/ContextualCompression/ContextualCompressionStrategy.cs`
- Create: `src/Rag.NET.QueryTechniques/ContextualCompression/ContextualCompressionOptions.cs`

No tests for this task — these are pure type definitions exercised by later tasks. Commit separately so subsequent tasks can reference a stable base.

**Step 1: Create `IContextualCompressor.cs`**

```csharp
using Rag.NET.Models;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Compresses retrieved chunks to only the content relevant to the query,
/// populating <see cref="SearchResult.CompressedText"/>. Non-destructive —
/// <see cref="TextChunk.Text"/> is never modified.
/// </summary>
public interface IContextualCompressor
{
    /// <summary>Compress each chunk's relevant content for <paramref name="query"/>.</summary>
    /// <remarks>
    /// Failing compression for an individual chunk is logged and returns the chunk
    /// with <see cref="SearchResult.CompressedText"/> set to <see langword="null"/> —
    /// the call never throws for per-chunk failures. Cancellation propagates.
    /// </remarks>
    ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default);
}
```

**Step 2: Create `ContextualCompressionStrategy.cs`**

```csharp
namespace Rag.NET.QueryTechniques.ContextualCompression;

public enum ContextualCompressionStrategy
{
    /// <summary>Embedding-similarity based, no LLM calls. Default.</summary>
    Extractive,

    /// <summary>Per-chunk LLM rewrite in parallel.</summary>
    Abstractive,
}
```

**Step 3: Create `ContextualCompressionOptions.cs`**

```csharp
namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Configuration for <see cref="IContextualCompressor"/>. Exactly one stopping
/// criterion (<see cref="KeepTopSentences"/> or <see cref="MaxTokensPerChunk"/>)
/// must be set — validated at registration time by the <c>UseContextualCompression</c>
/// extension.
/// </summary>
public sealed class ContextualCompressionOptions
{
    public ContextualCompressionStrategy Strategy { get; set; }
        = ContextualCompressionStrategy.Extractive;

    /// <summary>Keep the top-N most relevant sentences per chunk.</summary>
    /// <remarks>
    /// Precedence: when both this and <see cref="MaxTokensPerChunk"/> are set,
    /// <see cref="KeepTopSentences"/> wins (simpler mental model).
    /// </remarks>
    public int? KeepTopSentences { get; set; } = 3;

    /// <summary>
    /// Soft cap — keep highest-scoring sentences until the cap is reached.
    /// Uses <c>Microsoft.ML.Tokenizers</c> cl100k_base. Guideline, not a hard limit
    /// (abstractive mode may exceed it by a small margin).
    /// </summary>
    public int? MaxTokensPerChunk { get; set; }
}
```

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.QueryTechniques/Rag.NET.QueryTechniques.csproj -c Release`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

**Step 5: Commit**

```bash
git add src/Rag.NET.QueryTechniques/ContextualCompression/
git commit -m "feat(query-techniques): add IContextualCompressor abstraction + options"
```

---

## Task 4: `ExtractiveCompressor` — implementation + 5 tests

**Files:**
- Create: `src/Rag.NET.QueryTechniques/ContextualCompression/ExtractiveCompressor.cs`
- Create: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ExtractiveCompressorTests.cs`

**Step 1: Write all five failing tests**

Create `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ExtractiveCompressorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class ExtractiveCompressorTests
{
    // --- fixtures ---

    private static SearchResult MakeResult(string text, string docId = "d", int idx = 0) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = idx },
            Score = 0.5,
        };

    private static IEmbeddingGenerator<string, Embedding<float>> DeterministicEmbedder()
    {
        // Returns embeddings where the first dimension is the hash-based "topic" — sentences
        // mentioning "cats" all land near each other, sentences mentioning "rockets" land elsewhere.
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var inputs = ci.Arg<IEnumerable<string>>();
                var embeddings = inputs.Select(s =>
                {
                    var topic = s.Contains("cats", StringComparison.OrdinalIgnoreCase) ? 1f :
                                s.Contains("rockets", StringComparison.OrdinalIgnoreCase) ? -1f : 0f;
                    return new Embedding<float>(new[] { topic, 0f, 0f });
                }).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            });
        return embedder;
    }

    // --- tests ---

    [Fact]
    public async Task CompressAsync_TopNMode_KeepsHighestSimilaritySentences()
    {
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var chunk = "Cats purr loudly. Rockets go to Mars. Cats sleep often. Rockets have engines. Cats like fish.";
        var sources = new List<SearchResult> { MakeResult(chunk) };

        var result = await sut.CompressAsync(sources, "tell me about cats", TestContext.Current.CancellationToken);

        var compressed = result[0].CompressedText;
        Assert.NotNull(compressed);
        // top-2 by similarity to "cats" query: three "Cats..." sentences all score equally;
        // deterministic selection keeps the first two in original order.
        Assert.Contains("Cats", compressed, StringComparison.Ordinal);
        Assert.DoesNotContain("Rockets", compressed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressAsync_TokenBudgetMode_StopsAtBudget()
    {
        var opts = new ContextualCompressionOptions
        {
            KeepTopSentences = null,
            MaxTokensPerChunk = 10, // deliberately tiny
        };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var chunk = "Cats purr. Rockets go. Cats sleep. Rockets fly. Cats eat.";
        var sources = new List<SearchResult> { MakeResult(chunk) };

        var result = await sut.CompressAsync(sources, "tell me about cats", TestContext.Current.CancellationToken);

        var compressed = result[0].CompressedText;
        Assert.NotNull(compressed);
        // With a 10-token cap, we expect 1-2 short sentences to fit, all "Cats..." ones.
        Assert.Contains("Cats", compressed, StringComparison.Ordinal);
        Assert.DoesNotContain("Rockets", compressed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressAsync_EmbeddingFailure_ReturnsOriginalWithNullCompressedText()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<GeneratedEmbeddings<Embedding<float>>>>(_ => Task.FromException<GeneratedEmbeddings<Embedding<float>>>(new InvalidOperationException("embedder down")));
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(embedder, opts, NullLogger<ExtractiveCompressor>.Instance);
        var sources = new List<SearchResult> { MakeResult("Cats purr. Rockets fly.") };

        var result = await sut.CompressAsync(sources, "cats", TestContext.Current.CancellationToken);

        Assert.Null(result[0].CompressedText);
        Assert.Equal("Cats purr. Rockets fly.", result[0].Chunk.Text);
    }

    [Fact]
    public async Task CompressAsync_EmptyChunk_ReturnsNullCompressedText()
    {
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var sources = new List<SearchResult> { MakeResult("   ") };

        var result = await sut.CompressAsync(sources, "cats", TestContext.Current.CancellationToken);

        Assert.Null(result[0].CompressedText);
    }

    [Fact]
    public async Task CompressAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var opts = new ContextualCompressionOptions { KeepTopSentences = 2 };
        var sut = new ExtractiveCompressor(DeterministicEmbedder(), opts, NullLogger<ExtractiveCompressor>.Instance);
        var sources = new List<SearchResult> { MakeResult("Cats purr.") };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.CompressAsync(sources, "cats", cts.Token));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~ExtractiveCompressorTests" -v m`
Expected: FAIL — `ExtractiveCompressor` does not exist.

**Step 3: Implement `ExtractiveCompressor`**

Create `src/Rag.NET.QueryTechniques/ContextualCompression/ExtractiveCompressor.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using Rag.NET.Models;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Extractive contextual compressor. Splits each chunk into sentences, embeds them,
/// scores against the query embedding by cosine similarity, and keeps either the
/// top-N or as many top-ranked sentences as fit within the token budget.
/// </summary>
public sealed partial class ExtractiveCompressor : IContextualCompressor
{
    // Basic sentence splitter — sentences end with . ! ? followed by whitespace.
    // Good enough for v1; users needing linguistic-perfect splits can write their own compressor.
    private static readonly Regex SentenceSplit = SentenceSplitRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.Compiled)]
    private static partial Regex SentenceSplitRegex();

    private static readonly Tokenizer Cl100kTokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ContextualCompressionOptions _options;
    private readonly ILogger<ExtractiveCompressor> _logger;

    public ExtractiveCompressor(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ContextualCompressionOptions options,
        ILogger<ExtractiveCompressor>? logger = null)
    {
        _embedder = embedder;
        _options = options;
        _logger = logger ?? NullLogger<ExtractiveCompressor>.Instance;
    }

    public async ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0) return sources;

        // Embed the query once (shared across all chunks).
        Embedding<float> queryEmbedding;
        try
        {
            var qResult = await _embedder.GenerateAsync(new[] { query }, cancellationToken: cancellationToken).ConfigureAwait(false);
            queryEmbedding = qResult[0];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogQueryEmbeddingFailed(_logger, ex);
            return sources;
        }

        var tasks = sources.Select(s => CompressOneAsync(s, queryEmbedding, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<SearchResult> CompressOneAsync(
        SearchResult source,
        Embedding<float> queryEmbedding,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = source.Chunk.Text;
            if (string.IsNullOrWhiteSpace(text)) return source;

            var sentences = SentenceSplit.Split(text)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            if (sentences.Length == 0) return source;

            var sentEmbeddings = await _embedder.GenerateAsync(sentences, cancellationToken: cancellationToken).ConfigureAwait(false);

            var scores = new double[sentences.Length];
            for (var i = 0; i < sentences.Length; i++)
            {
                scores[i] = CosineSimilarity(queryEmbedding.Vector.Span, sentEmbeddings[i].Vector.Span);
            }

            var ranked = Enumerable.Range(0, sentences.Length)
                .OrderByDescending(i => scores[i])
                .ToArray();

            string compressed;
            if (_options.KeepTopSentences is { } topN)
            {
                var selected = ranked.Take(topN).OrderBy(i => i).Select(i => sentences[i]);
                compressed = string.Join(" ", selected);
            }
            else if (_options.MaxTokensPerChunk is { } maxTokens)
            {
                var kept = new List<int>();
                var running = 0;
                foreach (var idx in ranked)
                {
                    var tokens = Cl100kTokenizer.CountTokens(sentences[idx]);
                    if (running + tokens > maxTokens && kept.Count > 0) break;
                    kept.Add(idx);
                    running += tokens;
                    if (running >= maxTokens) break;
                }
                kept.Sort();
                compressed = string.Join(" ", kept.Select(i => sentences[i]));
            }
            else
            {
                // Both null — options-validation should catch this; defensive fallback.
                return source;
            }

            return string.IsNullOrWhiteSpace(compressed)
                ? source
                : source with { CompressedText = compressed };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogCompressionFailed(_logger, source.Chunk.DocumentId.Value, ex);
            return source;
        }
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Extractive compression failed for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogCompressionFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Extractive compression failed to embed query; returning all sources uncompressed.")]
    private static partial void LogQueryEmbeddingFailed(ILogger logger, Exception ex);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~ExtractiveCompressorTests" -v n`
Expected: All 5 tests PASS.

**Step 5: Verify zero warnings**

Run: `dotnet build src/Rag.NET.QueryTechniques/Rag.NET.QueryTechniques.csproj -c Release`
Expected: `0 Warning(s) 0 Error(s)`.

**Step 6: Commit**

```bash
git add src/Rag.NET.QueryTechniques/ContextualCompression/ExtractiveCompressor.cs tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ExtractiveCompressorTests.cs
git commit -m "feat(query-techniques): add ExtractiveCompressor (embedding similarity)"
```

---

## Task 5: `LlmAbstractiveCompressor` — implementation + 5 tests

**Files:**
- Create: `src/Rag.NET.QueryTechniques/ContextualCompression/LlmAbstractiveCompressor.cs`
- Create: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/LlmAbstractiveCompressorTests.cs`

**Step 1: Write all five failing tests**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class LlmAbstractiveCompressorTests
{
    private static SearchResult MakeResult(string text, string docId = "d", int idx = 0) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = idx },
            Score = 0.5,
        };

    private static ContextualCompressionOptions DefaultOpts() =>
        new() { Strategy = ContextualCompressionStrategy.Abstractive, KeepTopSentences = null, MaxTokensPerChunk = 200 };

    [Fact]
    public async Task CompressAsync_HappyPath_StoresLlmResponseInCompressedText()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of relevant content."))));
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);

        var result = await sut.CompressAsync(
            new List<SearchResult> { MakeResult("Long original chunk with many sentences. Some are relevant. Others are not.") },
            "relevant content",
            TestContext.Current.CancellationToken);

        Assert.Equal("Summary of relevant content.", result[0].CompressedText);
    }

    [Fact]
    public async Task CompressAsync_PerChunkParallelism_RunsConcurrentlyNotSequentially()
    {
        var gate = new TaskCompletionSource<bool>();
        var arrivals = 0;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref arrivals);
                await gate.Task.ConfigureAwait(false);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });

        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);
        var sources = Enumerable.Range(0, 5).Select(i => MakeResult($"chunk {i}", $"d{i}")).ToList();

        var compressTask = sut.CompressAsync(sources, "q", TestContext.Current.CancellationToken).AsTask();

        // Poll up to 2s for all 5 requests to fan out concurrently.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (arrivals < 5 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(5, arrivals);
        gate.SetResult(true);
        var result = await compressTask;
        Assert.All(result, r => Assert.Equal("ok", r.CompressedText));
    }

    [Fact]
    public async Task CompressAsync_OneChunkFails_OthersStillCompressed()
    {
        var call = 0;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var n = Interlocked.Increment(ref call);
                if (n == 2)
                    return Task.FromException<ChatResponse>(new InvalidOperationException("boom"));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"compressed-{n}")));
            });
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);

        var sources = Enumerable.Range(0, 3).Select(i => MakeResult($"c{i}", $"d{i}")).ToList();
        var result = await sut.CompressAsync(sources, "q", TestContext.Current.CancellationToken);

        // Exactly one of the three has CompressedText == null (the failure);
        // the other two have populated values.
        Assert.Equal(1, result.Count(r => r.CompressedText is null));
        Assert.Equal(2, result.Count(r => r.CompressedText is not null));
    }

    [Fact]
    public async Task CompressAsync_EmptyLlmResponse_FallsBackToNull()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))));
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);

        var result = await sut.CompressAsync(
            new List<SearchResult> { MakeResult("text") },
            "q",
            TestContext.Current.CancellationToken);

        Assert.Null(result[0].CompressedText);
    }

    [Fact]
    public async Task CompressAsync_CancelledToken_PropagatesOCE()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(ci => Task.FromException<ChatResponse>(new OperationCanceledException()));
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.CompressAsync(new List<SearchResult> { MakeResult("c") }, "q", cts.Token));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~LlmAbstractiveCompressorTests" -v m`
Expected: FAIL — `LlmAbstractiveCompressor` does not exist.

**Step 3: Implement `LlmAbstractiveCompressor`**

Create `src/Rag.NET.QueryTechniques/ContextualCompression/LlmAbstractiveCompressor.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Abstractive contextual compressor. Issues one <see cref="IChatClient"/> call per chunk
/// in parallel, asking the model to rewrite the chunk keeping only query-relevant content.
/// </summary>
public sealed partial class LlmAbstractiveCompressor : IContextualCompressor
{
    private readonly IChatClient _chatClient;
    private readonly ContextualCompressionOptions _options;
    private readonly ILogger<LlmAbstractiveCompressor> _logger;

    public LlmAbstractiveCompressor(
        IChatClient chatClient,
        ContextualCompressionOptions options,
        ILogger<LlmAbstractiveCompressor>? logger = null)
    {
        _chatClient = chatClient;
        _options = options;
        _logger = logger ?? NullLogger<LlmAbstractiveCompressor>.Instance;
    }

    public async ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0) return sources;

        var tasks = sources.Select(s => CompressOneAsync(s, query, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<SearchResult> CompressOneAsync(
        SearchResult source,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = BuildMessages(source.Chunk.Text, query);
            var response = await _chatClient.GetResponseAsync(messages, options: null, cancellationToken).ConfigureAwait(false);
            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptyResponse(_logger, source.Chunk.DocumentId.Value);
                return source;
            }
            return source with { CompressedText = text.Trim() };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogCompressionFailed(_logger, source.Chunk.DocumentId.Value, ex);
            return source;
        }
    }

    private IEnumerable<ChatMessage> BuildMessages(string content, string query)
    {
        var budget = _options.MaxTokensPerChunk is { } mt
            ? $" Target: at most {mt} tokens."
            : _options.KeepTopSentences is { } kn
                ? $" Keep at most {kn} sentences."
                : string.Empty;

        yield return new ChatMessage(ChatRole.System,
            "You compress retrieved content for a question-answering system. " +
            "Output only the content verbatim-style rewritten to retain information relevant to " +
            "the user's query. Do not include meta-commentary or markdown." + budget);

        yield return new ChatMessage(ChatRole.User,
            $"Query: {query}\n\nContent:\n{content}");
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Abstractive compression failed for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogCompressionFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Abstractive compression returned empty response for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogEmptyResponse(ILogger logger, string documentId);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~LlmAbstractiveCompressorTests" -v n`
Expected: All 5 tests PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET.QueryTechniques/ContextualCompression/LlmAbstractiveCompressor.cs tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/LlmAbstractiveCompressorTests.cs
git commit -m "feat(query-techniques): add LlmAbstractiveCompressor (per-chunk parallel LLM)"
```

---

## Task 6: Wire `IContextualCompressor` into `ChatAnswerEngine`

**Files:**
- Modify: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`
- Modify: `tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs` (or create)

**Step 1: Write three failing tests**

Add to `tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs` (create file if missing, adapt namespace):

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class ChatAnswerEngineCompressionTests
{
    private static SearchResult MakeResult(string chunkText, string? compressed) => new()
    {
        Chunk = new TextChunk { Text = chunkText, DocumentId = new DocumentId("d"), ChunkIndex = 0 },
        Score = 0.5,
        CompressedText = compressed,
    };

    private static IChatClient CapturingChatClient(List<ChatMessage> captured)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.AddRange(ci.Arg<IEnumerable<ChatMessage>>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
            });
        return chat;
    }

    [Fact]
    public async Task AskAsync_PrefersCompressedTextWhenPresent()
    {
        var captured = new List<ChatMessage>();
        var sut = new ChatAnswerEngine(CapturingChatClient(captured));
        var sources = new List<SearchResult> { MakeResult("ORIGINAL LONG TEXT", "compressed") };

        await sut.AskAsync("q", sources, new RagOptions(), TestContext.Current.CancellationToken);

        var user = captured.Single(m => m.Role == ChatRole.User);
        Assert.Contains("compressed", user.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("ORIGINAL LONG TEXT", user.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_FallsBackToChunkTextWhenCompressedTextNull()
    {
        var captured = new List<ChatMessage>();
        var sut = new ChatAnswerEngine(CapturingChatClient(captured));
        var sources = new List<SearchResult> { MakeResult("ORIGINAL LONG TEXT", compressed: null) };

        await sut.AskAsync("q", sources, new RagOptions(), TestContext.Current.CancellationToken);

        var user = captured.Single(m => m.Role == ChatRole.User);
        Assert.Contains("ORIGINAL LONG TEXT", user.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_SkipCompressionTrue_DoesNotInvokeCompressor()
    {
        var captured = new List<ChatMessage>();
        var compressor = Substitute.For<IContextualCompressor>();
        compressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                throw new InvalidOperationException("compressor should not be called");
            });
        var sut = new ChatAnswerEngine(CapturingChatClient(captured), memory: null, compressor: compressor);
        var sources = new List<SearchResult> { MakeResult("text", compressed: null) };

        await sut.AskAsync("q", sources, new RagOptions { SkipCompression = true }, TestContext.Current.CancellationToken);

        await compressor.DidNotReceive().CompressAsync(
            Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~ChatAnswerEngineCompressionTests" -v m`
Expected: compile errors — `ChatAnswerEngine` constructor has no `compressor` parameter.

**Step 3: Modify `ChatAnswerEngine`**

In `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs`:

1. Change the class declaration:
```csharp
public sealed class ChatAnswerEngine : IAnswerEngine
{
    private readonly IChatClient _chatClient;
    private readonly IConversationMemory? _memory;
    private readonly IContextualCompressor? _compressor;

    public ChatAnswerEngine(
        IChatClient chatClient,
        IConversationMemory? memory = null,
        IContextualCompressor? compressor = null)
    {
        _chatClient = chatClient;
        _memory = memory;
        _compressor = compressor;
    }
    // ... existing members rewritten to use fields instead of primary-ctor parameters ...
}
```

2. In `AskAsync` (and `AskStreamingAsync`), add compression **before** `BuildMessagesAsync`:

```csharp
public async Task<RagResponse> AskAsync(
    string query,
    IReadOnlyList<SearchResult> sources,
    RagOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var opts = options ?? new RagOptions();

    using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ask");
    activity?.SetTag("source.count", sources.Count);
    activity?.SetTag("synthesis.strategy", opts.SynthesisStrategy.ToString());

    // NEW: contextual compression (if configured and not skipped).
    if (_compressor is not null && !opts.SkipCompression)
    {
        sources = await _compressor.CompressAsync(sources, query, cancellationToken).ConfigureAwait(false);
    }

    var (messages, chatOptions) = await BuildMessagesAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);
    // ... rest unchanged ...
}
```

3. In `BuildMessagesAsync`, update the user message line:
```csharp
// Before:
//   sources.Select((s, i) => $"[Source {i + 1}]\n{s.Chunk.Text}")
// After:
    sources.Select((s, i) => $"[Source {i + 1}]\n{s.CompressedText ?? s.Chunk.Text}"));
```

4. Apply the same compression hook to `AskStreamingAsync` before its `BuildMessagesAsync` call.

5. Add `using Rag.NET.QueryTechniques.ContextualCompression;` to the top of the file.

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~ChatAnswerEngineCompressionTests" -v n`
Expected: All 3 tests PASS.

**Step 5: Verify no existing tests regressed**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release -v m`
Expected: Total pass count matches the baseline from before this change (773+ depending on current state), 0 failures.

**Step 6: Commit**

```bash
git add src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs tests/Rag.NET.Tests/AnswerGeneration/ChatAnswerEngineTests.cs
git commit -m "feat(answer-engine): wire IContextualCompressor into ChatAnswerEngine"
```

---

## Task 7: `ContextualCompressionRetrievalBehavior` + 1 test

**Files:**
- Create: `src/Rag.NET.QueryTechniques/ContextualCompression/ContextualCompressionRetrievalBehavior.cs`
- Create: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ContextualCompressionRetrievalBehaviorTests.cs`

**Step 1: Write failing test**

```csharp
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class ContextualCompressionRetrievalBehaviorTests
{
    [Fact]
    public async Task HandleAsync_InvokesCompressorOnPipelineResults()
    {
        var pipelineResults = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("d"), ChunkIndex = 0 }, Score = 0.5 },
        };
        var compressed = new List<SearchResult>
        {
            pipelineResults[0] with { CompressedText = "compressed" },
        };
        var compressor = Substitute.For<IContextualCompressor>();
        compressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<SearchResult>>(compressed));

        var sut = new ContextualCompressionRetrievalBehavior(compressor);
        var ctx = new RetrievalContext
        {
            Query = "q",
            Options = new Rag.NET.Models.Options.RetrievalOptions(),
            Extensions = new Dictionary<string, object>(StringComparer.Ordinal),
        };

        var result = await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (_, _) => new ValueTask<IReadOnlyList<SearchResult>>(pipelineResults));

        Assert.Equal("compressed", result[0].CompressedText);
    }
}
```

> **Note:** `RetrievalContext` field names may differ — check the existing `RedundancyFilterBehavior` test for the exact construction pattern and adapt.

**Step 2: Run to verify it fails**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~ContextualCompressionRetrievalBehaviorTests" -v m`
Expected: FAIL — type doesn't exist.

**Step 3: Implement**

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Retrieval;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Retrieval-pipeline wrapper around <see cref="IContextualCompressor"/> — runs
/// compression on the pipeline output so plain <c>RetrieveAsync</c> callers see
/// compressed text. Opt-in via <c>UseContextualCompressionInRetrieval()</c>.
/// </summary>
public sealed partial class ContextualCompressionRetrievalBehavior : IRetrievalBehavior
{
    private readonly IContextualCompressor _compressor;
    private readonly ILogger<ContextualCompressionRetrievalBehavior> _logger;

    public ContextualCompressionRetrievalBehavior(
        IContextualCompressor compressor,
        ILogger<ContextualCompressionRetrievalBehavior>? logger = null)
    {
        _compressor = compressor;
        _logger = logger ?? NullLogger<ContextualCompressionRetrievalBehavior>.Instance;
    }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        return await _compressor.CompressAsync(results, ctx.Query, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run test, expect PASS**

**Step 5: Commit**

```bash
git add src/Rag.NET.QueryTechniques/ContextualCompression/ContextualCompressionRetrievalBehavior.cs tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ContextualCompressionRetrievalBehaviorTests.cs
git commit -m "feat(query-techniques): add ContextualCompressionRetrievalBehavior"
```

---

## Task 8: `UseContextualCompression` extension + validation tests

**Files:**
- Modify: `src/Rag.NET.QueryTechniques/RagBuilderExtensions.cs`
- Create: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/RagBuilderExtensionsTests.cs`

**Step 1: Write 5 failing tests**

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.QueryTechniques;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class UseContextualCompressionExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddSingleton<IChatClient>(Substitute.For<IChatClient>());
        return services;
    }

    private static IRagBuilder BuilderOn(IServiceCollection services) =>
        new RagBuilder(services); // existing type used by AddRagNet

    [Fact]
    public void UseContextualCompression_WithoutStoppingCriteria_ThrowsOnRegistration()
    {
        var services = BaseServices();
        var builder = BuilderOn(services);

        Assert.Throws<InvalidOperationException>(() =>
            builder.UseContextualCompression(o => { o.KeepTopSentences = null; o.MaxTokensPerChunk = null; }));
    }

    [Fact]
    public void UseContextualCompression_NegativeValue_ThrowsOnRegistration()
    {
        var services = BaseServices();
        Assert.Throws<InvalidOperationException>(() =>
            BuilderOn(services).UseContextualCompression(o => o.KeepTopSentences = -1));
        Assert.Throws<InvalidOperationException>(() =>
            BuilderOn(services).UseContextualCompression(o => { o.KeepTopSentences = null; o.MaxTokensPerChunk = 0; }));
    }

    [Fact]
    public void UseContextualCompression_ExtractiveStrategy_RegistersExtractiveCompressor()
    {
        var services = BaseServices();
        BuilderOn(services).UseContextualCompression(o => o.Strategy = ContextualCompressionStrategy.Extractive);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IContextualCompressor>();
        Assert.IsType<ExtractiveCompressor>(resolved);
    }

    [Fact]
    public void UseContextualCompression_AbstractiveStrategy_RegistersLlmAbstractiveCompressor()
    {
        var services = BaseServices();
        BuilderOn(services).UseContextualCompression(o => o.Strategy = ContextualCompressionStrategy.Abstractive);

        using var sp = services.BuildServiceProvider();
        Assert.IsType<LlmAbstractiveCompressor>(sp.GetRequiredService<IContextualCompressor>());
    }

    [Fact]
    public void UseContextualCompression_DefaultsToExtractive()
    {
        var services = BaseServices();
        BuilderOn(services).UseContextualCompression();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<ExtractiveCompressor>(sp.GetRequiredService<IContextualCompressor>());
    }
}
```

**Step 2: Run tests, verify FAIL**

**Step 3: Implement extension**

In `src/Rag.NET.QueryTechniques/RagBuilderExtensions.cs`, add:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.AnswerGeneration;
using Rag.NET.QueryTechniques.ContextualCompression;

// ... existing usings kept ...

public static TBuilder UseContextualCompression<TBuilder>(
    this TBuilder builder,
    Action<ContextualCompressionOptions>? configure = null)
    where TBuilder : IRagBuilder
{
    var options = new ContextualCompressionOptions();
    configure?.Invoke(options);
    ValidateOptions(options);
    builder.Services.AddSingleton(options);

    if (options.Strategy == ContextualCompressionStrategy.Extractive)
    {
        builder.Services.AddSingleton<IContextualCompressor>(sp =>
            new ExtractiveCompressor(
                sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                sp.GetRequiredService<ContextualCompressionOptions>(),
                sp.GetService<ILogger<ExtractiveCompressor>>()));
    }
    else
    {
        builder.Services.AddSingleton<IContextualCompressor>(sp =>
            new LlmAbstractiveCompressor(
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<ContextualCompressionOptions>(),
                sp.GetService<ILogger<LlmAbstractiveCompressor>>()));
    }

    // Rebuild ChatAnswerEngine factory to inject the compressor.
    // Mirrors the EnsureChatAnswerEngine pattern from Rag.NET.Security.RagBuilderExtensions.
    builder.Services.AddSingleton<ChatAnswerEngine>(sp =>
        new ChatAnswerEngine(
            sp.GetRequiredService<IChatClient>(),
            sp.GetService<IConversationMemory>(),
            sp.GetService<IContextualCompressor>()));

    return builder;
}

private static void ValidateOptions(ContextualCompressionOptions opts)
{
    if (opts.KeepTopSentences is null && opts.MaxTokensPerChunk is null)
        throw new InvalidOperationException(
            "ContextualCompressionOptions: at least one of KeepTopSentences or MaxTokensPerChunk must be set.");
    if (opts.KeepTopSentences is { } n && n <= 0)
        throw new InvalidOperationException("ContextualCompressionOptions.KeepTopSentences must be positive.");
    if (opts.MaxTokensPerChunk is { } m && m <= 0)
        throw new InvalidOperationException("ContextualCompressionOptions.MaxTokensPerChunk must be positive.");
}
```

**Step 4: Run tests, expect PASS**

Run: `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release --filter "FullyQualifiedName~UseContextualCompressionExtensionsTests" -v n`

**Step 5: Commit**

```bash
git add src/Rag.NET.QueryTechniques/RagBuilderExtensions.cs tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/RagBuilderExtensionsTests.cs
git commit -m "feat(query-techniques): add UseContextualCompression DI extension"
```

---

## Task 9: `UseContextualCompressionInRetrieval` + 2 tests

**Files:**
- Modify: `src/Rag.NET.QueryTechniques/RagBuilderExtensions.cs`
- Append to: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/RagBuilderExtensionsTests.cs`

**Step 1: Write 2 failing tests**

Append to the existing test class:

```csharp
[Fact]
public void UseContextualCompressionInRetrieval_WithoutBaseRegistration_Throws()
{
    var services = BaseServices();
    services.AddRagNet(); // registers RetrievalPipelineBuilder
    var builder = BuilderOn(services);

    Assert.Throws<InvalidOperationException>(() => builder.UseContextualCompressionInRetrieval());
}

[Fact]
public void UseContextualCompressionInRetrieval_AddsBehaviorToPipelineBuilder()
{
    var services = BaseServices();
    services.AddRagNet(); // registers RetrievalPipelineBuilder in DI
    var builder = BuilderOn(services);
    builder.UseContextualCompression();

    builder.UseContextualCompressionInRetrieval();

    var pipelineBuilder = services
        .First(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
        .ImplementationInstance as RetrievalPipelineBuilder;
    Assert.NotNull(pipelineBuilder);
    var types = pipelineBuilder.GetBehaviorTypes();
    Assert.Contains(typeof(ContextualCompressionRetrievalBehavior), types);
}
```

> **Note:** If `RetrievalPipelineBuilder.GetBehaviorTypes()` is internal, add `[InternalsVisibleTo("Rag.NET.Tests")]` or use reflection in the test. Check the Task 2 of the AuditLog plan for an example of this pattern.

**Step 2: Run, verify FAIL**

**Step 3: Implement**

Append to `RagBuilderExtensions.cs`:

```csharp
public static TBuilder UseContextualCompressionInRetrieval<TBuilder>(this TBuilder builder)
    where TBuilder : IRagBuilder
{
    // Require UseContextualCompression to have been called first — it registers IContextualCompressor
    // and validates the options. A silent no-op here would be surprising.
    if (!builder.Services.Any(d => d.ServiceType == typeof(IContextualCompressor)))
        throw new InvalidOperationException(
            "UseContextualCompressionInRetrieval requires UseContextualCompression to be called first.");

    // Register the behavior as a concrete type so the retrieval pipeline can resolve it.
    builder.Services.AddSingleton<ContextualCompressionRetrievalBehavior>(sp =>
        new ContextualCompressionRetrievalBehavior(
            sp.GetRequiredService<IContextualCompressor>(),
            sp.GetService<ILogger<ContextualCompressionRetrievalBehavior>>()));

    // Insert into the retrieval pipeline before RetrievalGuardBehavior so compression sees the
    // post-reranking result set (but before auth/trust filtering, which may legitimately drop chunks).
    var pipelineBuilder = builder.Services
        .FirstOrDefault(d => d.ServiceType == typeof(RetrievalPipelineBuilder))
        ?.ImplementationInstance as RetrievalPipelineBuilder
        ?? throw new InvalidOperationException(
            "UseContextualCompressionInRetrieval requires AddRagNet to be called first " +
            "so that RetrievalPipelineBuilder is registered in DI.");

    pipelineBuilder.Add<ContextualCompressionRetrievalBehavior>(before: typeof(RetrievalGuardBehavior));

    return builder;
}
```

Add required usings:
- `Rag.NET.DependencyInjection;` (for `RetrievalPipelineBuilder`)
- `Rag.NET.Retrieval.Behaviors;` (for `RetrievalGuardBehavior`)

**Step 4: Run tests, expect PASS**

**Step 5: Commit**

```bash
git add src/Rag.NET.QueryTechniques/RagBuilderExtensions.cs tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/RagBuilderExtensionsTests.cs
git commit -m "feat(query-techniques): add UseContextualCompressionInRetrieval opt-in"
```

---

## Task 10: Integration test (end-to-end)

**Files:**
- Create: `tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ContextualCompressionIntegrationTests.cs`

**Step 1: Write failing test**

```csharp
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.QueryTechniques;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class ContextualCompressionIntegrationTests
{
    [Fact]
    public async Task AskAsync_WithExtractiveCompression_AnswerPromptContainsCompressedText()
    {
        // Arrange: in-memory services with a deterministic embedder and a capturing chat client.
        var services = new ServiceCollection();
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var embeddings = ci.Arg<IEnumerable<string>>().Select(s =>
                    new Embedding<float>(new[] { s.Contains("cats", StringComparison.OrdinalIgnoreCase) ? 1f : 0f, 0f, 0f })).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            });
        services.AddSingleton(embedder);
        var capturedMessages = new List<ChatMessage>();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { capturedMessages.AddRange(ci.Arg<IEnumerable<ChatMessage>>()); return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"))); });
        services.AddSingleton(chat);

        // Use the existing in-memory vector store so we don't need Docker.
        services.AddRagNet(rag => rag.UseContextualCompression(o => o.KeepTopSentences = 1));
        // (Assumes the in-memory vector store is registered by default. If not, register it here
        //  or adapt to whichever local store the codebase uses for tests.)

        await using var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<IRagPipeline>();

        var doc = "Cats purr loudly. Rockets go to Mars. Cats sleep often.";
        var docId = $"compr-{Guid.CreateVersion7():N}";
        await pipeline.IngestAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(doc)),
            new DocumentMetadata { DocumentId = new DocumentId(docId), FileName = "d.txt", ContentType = "text/plain" },
            cancellationToken: TestContext.Current.CancellationToken);

        // Act
        await pipeline.AskAsync("tell me about cats", cancellationToken: TestContext.Current.CancellationToken);

        // Assert: the user message sent to the chat client contains "Cats" but not "Rockets".
        var userMessage = capturedMessages.Single(m => m.Role == ChatRole.User).Text ?? "";
        Assert.Contains("Cats", userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Rockets", userMessage, StringComparison.Ordinal);
    }
}
```

> **Note:** If `AddRagNet` does not register a default `IVectorStore` for in-memory use, this test needs adaptation. Options:
> - Look for an existing `InMemoryVectorStore` in the codebase and register it manually.
> - Failing that, move this test to `Rag.NET.Security.IntegrationTests`-style with `[Collection("PgVector")]` and use the real PgVector fixture.

**Step 2: Run, verify FAIL then PASS after the previous tasks' code is in place.**

**Step 3: Commit**

```bash
git add tests/Rag.NET.Tests/QueryTechniques/ContextualCompression/ContextualCompressionIntegrationTests.cs
git commit -m "test(query-techniques): add contextual compression integration test"
```

---

## Task 11: Benchmarks (extractive only)

**Files:**
- Create: `benchmarks/Rag.NET.Benchmarks/ContextualCompressionBenchmarks.cs`

**Step 1: Inspect existing benchmark patterns**

Read `benchmarks/Rag.NET.Benchmarks/SecurityBenchmarks.cs` to match style (attributes, memory diagnoser, baseline marker).

**Step 2: Create the benchmark class**

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class ContextualCompressionBenchmarks
{
    private ExtractiveCompressor _topN = null!;
    private ExtractiveCompressor _tokenBudget = null!;
    private List<SearchResult> _small = null!;
    private List<SearchResult> _large = null!;

    [GlobalSetup]
    public void Setup()
    {
        var embedder = new FakeEmbedder();
        _topN = new ExtractiveCompressor(embedder, new ContextualCompressionOptions { KeepTopSentences = 3 }, NullLogger<ExtractiveCompressor>.Instance);
        _tokenBudget = new ExtractiveCompressor(embedder, new ContextualCompressionOptions { KeepTopSentences = null, MaxTokensPerChunk = 50 }, NullLogger<ExtractiveCompressor>.Instance);

        _small = Enumerable.Range(0, 5).Select(i => Result("Short sentence about topic " + i + ". Another short one. And one more.", "d" + i)).ToList();
        _large = Enumerable.Range(0, 5).Select(i =>
        {
            var sb = new System.Text.StringBuilder();
            for (var j = 0; j < 50; j++) sb.Append("Long paragraph sentence number ").Append(j).Append(". ");
            return Result(sb.ToString(), "d" + i);
        }).ToList();
    }

    [Benchmark]
    public async Task TopN_SmallChunks() =>
        _ = await _topN.CompressAsync(_small, "query", CancellationToken.None);

    [Benchmark]
    public async Task TopN_LargeChunks() =>
        _ = await _topN.CompressAsync(_large, "query", CancellationToken.None);

    [Benchmark]
    public async Task TokenBudget_LargeChunks() =>
        _ = await _tokenBudget.CompressAsync(_large, "query", CancellationToken.None);

    private static SearchResult Result(string text, string id) =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(id), ChunkIndex = 0 }, Score = 0.5 };

    private sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f })).ToList()));
        public EmbeddingGeneratorMetadata Metadata => new("fake", null, null, 3);
        public TService? GetService<TService>(object? key = null) where TService : class => null;
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
```

**Step 3: Verify it builds**

Run: `dotnet build benchmarks/Rag.NET.Benchmarks/Rag.NET.Benchmarks.csproj -c Release`
Expected: `0 Warning(s) 0 Error(s)`.

**Step 4: Commit** (don't run the benchmarks — CI-free; just make sure they compile)

```bash
git add benchmarks/Rag.NET.Benchmarks/ContextualCompressionBenchmarks.cs
git commit -m "bench(query-techniques): add ExtractiveCompressor benchmarks"
```

---

## Task 12: Update `docs/reference/features.md`

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Update the "Contextual Compression" entry details**

In `docs/reference/features.md`, find the `### Contextual Compression` section (around line 747). Update it to describe the shipped implementation — package name (`Rag.NET.QueryTechniques`), strategies (extractive + abstractive), stopping criteria (top-N + token budget), non-destructive output, opt-out via `RagOptions.SkipCompression`.

**Step 2: Flip the priority-table entry**

In the `## Priority / Dependencies` table (line ~1031), change:
```
| [ ] | Contextual Compression | Medium | `IChatClient` or embeddings |
```
to:
```
| [x] | Contextual Compression | Medium | `IChatClient` or embeddings |
```

**Step 3: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs(features): mark Contextual Compression as shipped"
```

---

## Final verification

After all 12 tasks:

1. `dotnet build -c Release` on the solution root → `0 Warning(s) 0 Error(s)`.
2. `dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -c Release` → baseline count + 20-ish new tests, 0 failures.
3. `dotnet test tests/Rag.NET.Security.IntegrationTests/Rag.NET.Security.IntegrationTests.csproj -c Release` → 7/7 still pass (regression check).
4. Open `docs/reference/features.md` — the Contextual Compression entry reflects reality.
5. `git log --oneline -13` shows 12 commits, each touching one clear slice of functionality.
