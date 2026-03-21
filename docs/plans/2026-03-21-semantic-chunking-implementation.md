# Semantic Chunking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `SemanticChunkingStrategy` that splits text at meaning boundaries using embedding similarity between consecutive sentences.

**Architecture:** New `IChunkingStrategy` implementation that sentence-splits, embeds all sentences in one batch, computes consecutive cosine similarities, breaks at the bottom-percentile boundaries, then enforces min/max size constraints. Uses the same `IEmbeddingGenerator` from DI by default, with an optional override.

**Tech Stack:** .NET 10, `Microsoft.Extensions.AI` (`IEmbeddingGenerator`), xunit.v3, NSubstitute

---

### Task 1: `SemanticChunkingOptions` model

**Files:**
- Create: `src/Rag.NET/Models/Options/SemanticChunkingOptions.cs`
- Test: `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs` (scaffold)

**Step 1: Write the failing test**

Create `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs`:

```csharp
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticChunkingStrategyTests
{
    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var opts = new SemanticChunkingOptions();
        Assert.Equal(0.25f, opts.BreakpointPercentile);
        Assert.Equal(100, opts.MinChunkSize);
        Assert.Equal(1500, opts.MaxChunkSize);
        Assert.Null(opts.ChunkingEmbedder);
    }
}
```

**Step 2: Run test to verify it fails**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```
Expected: FAIL — `SemanticChunkingOptions` not found.

**Step 3: Create `SemanticChunkingOptions.cs`**

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Models.Options;

public sealed class SemanticChunkingOptions
{
    /// <summary>
    /// Breakpoint percentile for similarity scores. Consecutive sentence pairs with
    /// similarity in the bottom N percentile are treated as chunk boundaries.
    /// Lower = fewer breaks (larger chunks), higher = more breaks (smaller chunks).
    /// Default 0.25 (bottom 25%).
    /// </summary>
    public float BreakpointPercentile { get; init; } = 0.25f;

    /// <summary>
    /// Minimum chunk size in characters. Chunks smaller than this are merged with
    /// their nearest neighbor. Default 100.
    /// </summary>
    public int MinChunkSize { get; init; } = 100;

    /// <summary>
    /// Maximum chunk size in characters. Chunks exceeding this are split at
    /// sentence boundaries. Default 1500.
    /// </summary>
    public int MaxChunkSize { get; init; } = 1500;

    /// <summary>
    /// Optional embedding model override for chunking only. When null (default),
    /// uses the same <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> registered
    /// for retrieval. Set this when you want a smaller/faster model for chunking
    /// (e.g., MiniLM) while keeping a larger model for retrieval quality.
    /// </summary>
    public IEmbeddingGenerator<string, Embedding<float>>? ChunkingEmbedder { get; init; }
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```
Expected: PASS (1 test).

**Step 5: Commit**

```bash
git add src/Rag.NET/Models/Options/SemanticChunkingOptions.cs \
        tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs
git commit -m "feat: add SemanticChunkingOptions model"
```

---

### Task 2: Sentence splitter and cosine similarity helpers

**Files:**
- Create: `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs` (helpers only, `ChunkAsync` stubbed)
- Modify: `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs`

**Step 1: Write the failing tests**

Add to the test file:

```csharp
[Theory]
[InlineData("Hello world. How are you? Fine thanks!", 3)]
[InlineData("Single sentence without ending punctuation", 1)]
[InlineData("Dr. Smith went to Washington. He met Mr. Jones.", 2)]
[InlineData("", 0)]
[InlineData("First sentence. Second sentence. Third sentence.", 3)]
public void SplitSentences_VariousInputs_ReturnsExpectedCount(string text, int expectedCount)
{
    var sentences = SemanticChunkingStrategy.SplitSentences(text);
    Assert.Equal(expectedCount, sentences.Count);
}

[Fact]
public void SplitSentences_PreservesAbbreviations()
{
    var sentences = SemanticChunkingStrategy.SplitSentences(
        "Dr. Smith e.g. the doctor went home. Then he slept.");
    Assert.Equal(2, sentences.Count);
    Assert.Contains("Dr. Smith", sentences[0]);
}

[Fact]
public void CosineSimilarity_IdenticalVectors_ReturnsOne()
{
    var a = new float[] { 1f, 0f, 0f };
    var b = new float[] { 1f, 0f, 0f };
    var sim = SemanticChunkingStrategy.CosineSimilarity(a, b);
    Assert.Equal(1.0, sim, precision: 5);
}

[Fact]
public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
{
    var a = new float[] { 1f, 0f };
    var b = new float[] { 0f, 1f };
    var sim = SemanticChunkingStrategy.CosineSimilarity(a, b);
    Assert.Equal(0.0, sim, precision: 5);
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```

**Step 3: Create `SemanticChunkingStrategy.cs` with helpers**

```csharp
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed partial class SemanticChunkingStrategy(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    SemanticChunkingOptions options) : IChunkingStrategy
{
    // Matches sentence-ending punctuation followed by whitespace,
    // with negative lookbehind for common abbreviations.
    [GeneratedRegex(@"(?<!\b(?:Mr|Mrs|Ms|Dr|Jr|Sr|vs|etc|e\.g|i\.e))\.\s+|[!?]\s+", RegexOptions.Compiled)]
    private static partial Regex SentenceEndPattern();

    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var parts = SentenceEndPattern().Split(text);
        var sentences = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                sentences.Add(trimmed);
        }
        return sentences;
    }

    internal static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotImplementedException("Full implementation in Task 3.");
    }
}
```

> **Note:** The class uses `partial` for `[GeneratedRegex]`. `SplitSentences` and `CosineSimilarity` are `internal static` for testability (tests access via `InternalsVisibleTo`).

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET/Chunking/SemanticChunkingStrategy.cs \
        tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs
git commit -m "feat: add sentence splitter and cosine similarity helpers"
```

---

### Task 3: Implement `ChunkAsync` core algorithm

**Files:**
- Modify: `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs`
- Modify: `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs`

**Step 1: Write the failing tests**

Add tests that use a mock `IEmbeddingGenerator` to control similarity:

```csharp
// Helper: creates an embedder that returns predetermined vectors for each sentence
private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(
    params float[][] vectors)
{
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    embedder.GenerateAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<EmbeddingGenerationOptions?>(),
            Arg.Any<CancellationToken>())
        .Returns(ci =>
        {
            var inputs = ci.Arg<IEnumerable<string>>().ToList();
            var result = new GeneratedEmbeddings<Embedding<float>>();
            for (int i = 0; i < inputs.Count; i++)
                result.Add(new Embedding<float>(i < vectors.Length ? vectors[i] : vectors[^1]));
            return Task.FromResult(result);
        });
    return embedder;
}

[Fact]
public async Task ChunkAsync_TwoDifferentTopics_BreaksBetweenThem()
{
    var ct = TestContext.Current.CancellationToken;
    // Two similar sentences, then one very different
    var embedder = MockEmbedder(
        [1f, 0f, 0f],  // sentence 1 — topic A
        [0.9f, 0.1f, 0f],  // sentence 2 — topic A (similar)
        [0f, 0f, 1f]); // sentence 3 — topic B (different)

    var opts = new SemanticChunkingOptions { BreakpointPercentile = 0.5f, MinChunkSize = 1, MaxChunkSize = 5000 };
    var sut = new SemanticChunkingStrategy(embedder, opts);
    var section = new DocumentSection
    {
        Text = "Topic A first. Topic A second. Topic B entirely different.",
        DocumentId = new DocumentId("doc-1"),
    };

    var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    Assert.Equal(2, chunks.Count);
    Assert.Contains("Topic A", chunks[0].Text);
    Assert.Contains("Topic B", chunks[1].Text);
}

[Fact]
public async Task ChunkAsync_SingleSentence_ReturnsOneChunk_NoEmbeddingCall()
{
    var ct = TestContext.Current.CancellationToken;
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    var opts = new SemanticChunkingOptions();
    var sut = new SemanticChunkingStrategy(embedder, opts);
    var section = new DocumentSection
    {
        Text = "Just one sentence here",
        DocumentId = new DocumentId("doc-1"),
    };

    var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    Assert.Single(chunks);
    await embedder.DidNotReceive().GenerateAsync(
        Arg.Any<IEnumerable<string>>(),
        Arg.Any<EmbeddingGenerationOptions?>(),
        Arg.Any<CancellationToken>());
}

[Fact]
public async Task ChunkAsync_EmptyText_ReturnsNoChunks()
{
    var ct = TestContext.Current.CancellationToken;
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    var sut = new SemanticChunkingStrategy(embedder, new SemanticChunkingOptions());
    var section = new DocumentSection { Text = "", DocumentId = new DocumentId("doc-1") };

    var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    Assert.Empty(chunks);
}

[Fact]
public async Task ChunkAsync_UniformSimilarity_FewOrNoBreaks()
{
    var ct = TestContext.Current.CancellationToken;
    // All sentences have nearly identical embeddings
    var embedder = MockEmbedder(
        [1f, 0f], [0.99f, 0.01f], [0.98f, 0.02f], [0.97f, 0.03f]);
    var opts = new SemanticChunkingOptions { BreakpointPercentile = 0.25f, MinChunkSize = 1, MaxChunkSize = 5000 };
    var sut = new SemanticChunkingStrategy(embedder, opts);
    var section = new DocumentSection
    {
        Text = "Same topic one. Same topic two. Same topic three. Same topic four.",
        DocumentId = new DocumentId("doc-1"),
    };

    var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    // With 4 sentences and 3 similarities, bottom 25% = at most 1 break → at most 2 chunks
    Assert.InRange(chunks.Count, 1, 2);
}

[Fact]
public async Task ChunkAsync_CustomChunkingEmbedder_UsesOverride()
{
    var ct = TestContext.Current.CancellationToken;
    var defaultEmbedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    var customEmbedder = MockEmbedder([1f, 0f], [0f, 1f]);

    var opts = new SemanticChunkingOptions
    {
        ChunkingEmbedder = customEmbedder,
        BreakpointPercentile = 0.5f,
        MinChunkSize = 1,
        MaxChunkSize = 5000,
    };
    var sut = new SemanticChunkingStrategy(defaultEmbedder, opts);
    var section = new DocumentSection
    {
        Text = "First sentence. Second sentence.",
        DocumentId = new DocumentId("doc-1"),
    };

    _ = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    // Custom embedder was called, default was not
    await customEmbedder.Received(1).GenerateAsync(
        Arg.Any<IEnumerable<string>>(),
        Arg.Any<EmbeddingGenerationOptions?>(),
        Arg.Any<CancellationToken>());
    await defaultEmbedder.DidNotReceive().GenerateAsync(
        Arg.Any<IEnumerable<string>>(),
        Arg.Any<EmbeddingGenerationOptions?>(),
        Arg.Any<CancellationToken>());
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```

**Step 3: Implement `ChunkAsync`**

Replace the stub in `SemanticChunkingStrategy.cs`. The algorithm:

1. Sentence split → if 0, yield break; if 1, yield single chunk
2. Embed all sentences using `options.ChunkingEmbedder ?? embedder`
3. Compute consecutive cosine similarities (N-1 values)
4. Find breakpoint threshold: sort copy, index at `floor(percentile * length)`
5. Group sentences between breakpoints
6. Enforce min/max: merge small chunks with smaller neighbor, split oversized at sentence boundaries
7. Yield `TextChunk` per group with `DocumentId`, `ChunkIndex`, `StartPosition`, `EndPosition`

For `StartPosition`/`EndPosition`: track character offsets by accumulating sentence lengths + separator lengths in the original text.

Clamp `BreakpointPercentile` to `(0, 1)` at the start of `ChunkAsync`.

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```
Expected: All tests pass.

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName!~PgVector"
```

**Step 6: Commit**

```bash
git add src/Rag.NET/Chunking/SemanticChunkingStrategy.cs \
        tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs
git commit -m "feat: implement SemanticChunkingStrategy.ChunkAsync with breakpoint detection"
```

---

### Task 4: Min/max enforcement and `UseSemanticChunking` registration

**Files:**
- Modify: `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs` (if min/max not yet tested)
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Modify: `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task ChunkAsync_ChunkBelowMinSize_MergedWithNeighbor()
{
    var ct = TestContext.Current.CancellationToken;
    // 3 sentences: first two similar, third different — but first sentence is tiny
    var embedder = MockEmbedder([1f, 0f], [1f, 0f], [0f, 1f]);
    var opts = new SemanticChunkingOptions
    {
        BreakpointPercentile = 0.5f,
        MinChunkSize = 200,  // force merge — individual sentences are < 200 chars
        MaxChunkSize = 5000,
    };
    var sut = new SemanticChunkingStrategy(embedder, opts);
    var section = new DocumentSection
    {
        Text = "Short. Also short. Very different topic here with more words.",
        DocumentId = new DocumentId("doc-1"),
    };

    var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    // All should be merged into 1 chunk since each is below MinChunkSize
    Assert.Single(chunks);
}

[Fact]
public async Task ChunkAsync_ChunkAboveMaxSize_SplitAtSentenceBoundary()
{
    var ct = TestContext.Current.CancellationToken;
    // 4 sentences all similar (no breakpoints) — total exceeds MaxChunkSize
    var embedder = MockEmbedder([1f, 0f], [1f, 0f], [1f, 0f], [1f, 0f]);
    var longSentence = new string('a', 400);
    var text = $"{longSentence}. {longSentence}. {longSentence}. {longSentence}.";
    var opts = new SemanticChunkingOptions
    {
        BreakpointPercentile = 0.25f,
        MinChunkSize = 1,
        MaxChunkSize = 500,
    };
    var sut = new SemanticChunkingStrategy(embedder, opts);
    var section = new DocumentSection { Text = text, DocumentId = new DocumentId("doc-1") };

    var chunks = await sut.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

    Assert.True(chunks.Count > 1);
    Assert.All(chunks, c => Assert.True(c.Text.Length <= opts.MaxChunkSize + 50)); // small tolerance
}

[Fact]
public void UseSemanticChunking_RegistersStrategyAndOptions()
{
    var services = new ServiceCollection();
    var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    services.AddSingleton(embedder);
    services.AddRagNet(rag => rag.UseSemanticChunking());

    var provider = services.BuildServiceProvider();
    var strategy = provider.GetService<IChunkingStrategy>();
    var options = provider.GetService<SemanticChunkingOptions>();

    Assert.IsType<SemanticChunkingStrategy>(strategy);
    Assert.NotNull(options);
}
```

**Step 2: Run tests to verify they fail**

**Step 3: Add `UseSemanticChunking` to `RagBuilder`**

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`:

```csharp
/// <summary>
/// Registers <see cref="SemanticChunkingStrategy"/> which splits text at meaning
/// boundaries using embedding similarity between consecutive sentences.
/// Uses the same <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> registered for
/// retrieval by default. Override via <see cref="SemanticChunkingOptions.ChunkingEmbedder"/>
/// when you want a smaller/faster model for chunking only.
/// </summary>
public RagBuilder UseSemanticChunking(Action<SemanticChunkingOptions>? configure = null)
{
    var options = new SemanticChunkingOptions();
    configure?.Invoke(options);
    Services.AddSingleton(options);
    Services.AddSingleton<IChunkingStrategy, SemanticChunkingStrategy>();
    return this;
}
```

> **Note:** `SemanticChunkingOptions` uses `init`-only properties. The `configure` delegate pattern won't work with `init`. Check the actual property declarations — if they use `init`, change the `UseSemanticChunking` method to accept `SemanticChunkingOptions? options = null` directly (same pattern as `AudioParserBuilderExtensions`). If they use `set`, the `Action<>` pattern works.

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests --filter "SemanticChunkingStrategyTests"
```

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName!~PgVector"
```

**Step 6: Commit**

```bash
git add src/Rag.NET/Chunking/SemanticChunkingStrategy.cs \
        src/Rag.NET/DependencyInjection/RagBuilder.cs \
        tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyTests.cs
git commit -m "feat: add min/max enforcement and UseSemanticChunking registration"
```

---

### Task 5: Full solution test suite green check

**Step 1:** `dotnet test --filter "FullyQualifiedName!~PgVector"`

Expected: All tests pass.

---

## Summary

| Task | Key Change |
|------|-----------|
| 1 | `SemanticChunkingOptions` model |
| 2 | Sentence splitter + cosine similarity helpers |
| 3 | `ChunkAsync` core algorithm with breakpoint detection |
| 4 | Min/max enforcement + `UseSemanticChunking` DI registration |
| 5 | Full suite green |
