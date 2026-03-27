# Semantic Chunking Refinement — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extend `SemanticChunkingStrategy` with document-level chunking (`IDocumentChunkingStrategy`) and a new `IChunkRefinementStrategy` post-processing extension point; wire both into `ParseBehavior` and `RagBuilder`.

**Architecture:** `SemanticChunkingStrategy` gains two new interface implementations on the same class. `ParseBehavior` gains an optional nullable `[Inject]` property for `IChunkRefinementStrategy` that runs a refinement pass after chunking when present. `RagBuilder.UseSemanticChunking` registers all three interfaces; new `UseSemanticRefinement` registers only `IChunkRefinementStrategy`.

**Tech Stack:** .NET 9, ZeroAlloc.Inject (`[Singleton]`/`[Inject]`), Microsoft.Extensions.AI (`IEmbeddingGenerator`), xUnit, NSubstitute.

---

### Task 1: `IChunkRefinementStrategy` interface

**Files:**
- Create: `src/Rag.NET/Abstractions/IChunkRefinementStrategy.cs`
- Test: `tests/Rag.NET.Tests/Chunking/SemanticRefinementStrategyTests.cs` (stub — just namespace/class, filled in Task 3)

**Step 1: Create the interface file**

```csharp
using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Post-processes chunks produced by any chunking strategy.
/// Applied by ParseBehavior after the chunking step (both per-section and document-level paths).
/// </summary>
public interface IChunkRefinementStrategy
{
    IAsyncEnumerable<TextChunk> RefineAsync(
        IAsyncEnumerable<TextChunk> chunks,
        CancellationToken cancellationToken = default);
}
```

**Step 2: Build to verify it compiles**

```bash
dotnet build src/Rag.NET/Rag.NET.csproj
```
Expected: `Build succeeded`

**Step 3: Commit**

```bash
git add src/Rag.NET/Abstractions/IChunkRefinementStrategy.cs
git commit -m "feat: add IChunkRefinementStrategy interface"
```

---

### Task 2: `SemanticChunkingStrategy.ChunkDocumentAsync` (IDocumentChunkingStrategy)

**Files:**
- Modify: `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs`
- Create: `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyDocumentTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyDocumentTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticChunkingStrategyDocumentTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(params float[][] vectors)
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

    private static DocumentSection Section(string docId, string text) =>
        new() { DocumentId = new DocumentId(docId), Text = text };

    private static async IAsyncEnumerable<DocumentSection> ToAsync(
        IEnumerable<DocumentSection> sections)
    {
        foreach (var s in sections) yield return s;
    }

    [Fact]
    public void SemanticChunkingStrategy_ImplementsIDocumentChunkingStrategy()
    {
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());
        Assert.IsAssignableFrom<IDocumentChunkingStrategy>(strategy);
    }

    [Fact]
    public async Task ChunkDocumentAsync_EmptySections_ProducesNoChunks()
    {
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync([]), new ChunkingOptions()).ToListAsync();

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunkDocumentAsync_SimilarSections_MergeIntoOneChunk()
    {
        // Both sections get identical embeddings → similarity = 1.0 → no breakpoint → one chunk
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f]),
            new SemanticChunkingOptions { MinChunkSize = 1 });

        var sections = new[]
        {
            Section("doc1", "Alpha text here."),
            Section("doc1", "Beta text there."),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions()).ToListAsync();

        Assert.Single(chunks);
        Assert.Contains("Alpha", chunks[0].Text);
        Assert.Contains("Beta", chunks[0].Text);
    }

    [Fact]
    public async Task ChunkDocumentAsync_DissimilarSections_ProduceSeparateChunks()
    {
        // Orthogonal embeddings → similarity = 0 → breakpoint → two chunks
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [0f, 1f]),
            new SemanticChunkingOptions { MinChunkSize = 1 });

        var sections = new[]
        {
            Section("doc1", "Alpha topic sentence one."),
            Section("doc1", "Beta topic sentence two."),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions()).ToListAsync();

        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task ChunkDocumentAsync_OversizedGroup_SplitsChunk()
    {
        // One large section that exceeds MaxChunkSize → must split
        var opts = new SemanticChunkingOptions { MinChunkSize = 1, MaxChunkSize = 20 };
        var strategy = new SemanticChunkingStrategy(MockEmbedder([1f, 0f]), opts);

        var sections = new[]
        {
            Section("doc1", new string('A', 50)),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions()).ToListAsync();

        Assert.All(chunks, c => Assert.True(c.Text.Length <= opts.MaxChunkSize));
    }

    [Fact]
    public async Task ChunkDocumentAsync_UndersizedGroup_MergesWithNeighbor()
    {
        // Three sections: tiny, tiny, large; tiny sections should merge due to MinChunkSize
        // All identical embeddings to avoid breakpoints; rely on merge pass only
        var opts = new SemanticChunkingOptions { MinChunkSize = 30 };
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [1f, 0f], [1f, 0f]),
            opts);

        var sections = new[]
        {
            Section("doc1", "Hi."),
            Section("doc1", "Ok."),
            Section("doc1", "This is a longer section with sufficient content to meet min."),
        };

        var chunks = await strategy.ChunkDocumentAsync(
            ToAsync(sections), new ChunkingOptions()).ToListAsync();

        // Tiny sections should have been merged; expect fewer than 3 chunks
        Assert.True(chunks.Count < 3);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SemanticChunkingStrategyDocumentTests" --no-build 2>&1 | tail -5
```
Expected: compile error — `ChunkDocumentAsync` does not exist yet; or type assertion failure for `IDocumentChunkingStrategy`

**Step 3: Implement `ChunkDocumentAsync` on `SemanticChunkingStrategy`**

Change the class declaration in [SemanticChunkingStrategy.cs](src/Rag.NET/Chunking/SemanticChunkingStrategy.cs):

```csharp
public sealed partial class SemanticChunkingStrategy(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    SemanticChunkingOptions options) : IChunkingStrategy, IDocumentChunkingStrategy
```

Add a using at the top:
```csharp
using System.Runtime.CompilerServices;
```
(already present — verify)

Add `ChunkDocumentAsync` after the existing `ChunkAsync` method (before the private helpers):

```csharp
public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
    IAsyncEnumerable<DocumentSection> sections,
    ChunkingOptions chunkingOptions,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var sectionList = new List<DocumentSection>();
    await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        sectionList.Add(section);

    if (sectionList.Count == 0)
        yield break;

    cancellationToken.ThrowIfCancellationRequested();

    var activeEmbedder = _options.ChunkingEmbedder ?? _embedder;
    var texts = sectionList.ConvertAll(s => s.Text);
    var embeddings = await activeEmbedder.GenerateAsync(texts, cancellationToken: cancellationToken)
        .ConfigureAwait(false);

    // Consecutive cosine similarities between adjacent section embeddings
    var similarities = new double[sectionList.Count - 1];
    for (int i = 0; i < similarities.Length; i++)
        similarities[i] = CosineSimilarity(embeddings[i].Vector.Span, embeddings[i + 1].Vector.Span);

    // Group adjacent sections using the same percentile breakpoint logic as sentence path
    var percentile = Math.Clamp(_options.BreakpointPercentile, 0.01f, 0.99f);
    var sorted = similarities.OrderBy(s => s).ToArray();
    var thresholdIndex = (int)Math.Floor(percentile * sorted.Length);
    var threshold = sorted.Length > 0 ? sorted[Math.Min(thresholdIndex, sorted.Length - 1)] : 0;

    var groups = new List<List<DocumentSection>> { new() { sectionList[0] } };
    for (int i = 0; i < similarities.Length; i++)
    {
        if (similarities[i] < threshold)
            groups.Add(new List<DocumentSection>());
        groups[^1].Add(sectionList[i + 1]);
    }

    // Merge/split groups based on total text length — reuse sentence-level helpers via adapter
    var textGroups = groups.ConvertAll(g => g.ConvertAll(s => s.Text));
    MergeUndersizedGroups(textGroups);
    SplitOversizedGroups(textGroups);

    var documentId = sectionList[0].DocumentId;
    int chunkIndex = 0;
    foreach (var group in textGroups)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = string.Join("\n\n", group);
        if (string.IsNullOrWhiteSpace(text))
            continue;

        yield return new TextChunk
        {
            Text = text,
            DocumentId = documentId,
            ChunkIndex = chunkIndex++,
            StartPosition = 0,
            EndPosition = text.Length,
        };
    }
}
```

**Note:** `MergeUndersizedGroups` and `SplitOversizedGroups` currently take `List<List<string>>`, which is exactly what `textGroups` is. No change needed to those helpers.

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SemanticChunkingStrategyDocumentTests"
```
Expected: all 6 tests pass

**Step 5: Run full suite to check for regressions**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: all tests pass

**Step 6: Commit**

```bash
git add src/Rag.NET/Chunking/SemanticChunkingStrategy.cs
git add tests/Rag.NET.Tests/Chunking/SemanticChunkingStrategyDocumentTests.cs
git commit -m "feat: implement SemanticChunkingStrategy.ChunkDocumentAsync (IDocumentChunkingStrategy)"
```

---

### Task 3: `SemanticChunkingStrategy.RefineAsync` (IChunkRefinementStrategy)

**Files:**
- Modify: `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs`
- Create: `tests/Rag.NET.Tests/Chunking/SemanticRefinementStrategyTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Chunking/SemanticRefinementStrategyTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Chunking;

public class SemanticRefinementStrategyTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MockEmbedder(params float[][] vectors)
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

    private static async IAsyncEnumerable<TextChunk> ToAsync(IEnumerable<TextChunk> chunks)
    {
        foreach (var c in chunks) yield return c;
    }

    [Fact]
    public void SemanticChunkingStrategy_ImplementsIChunkRefinementStrategy()
    {
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());
        Assert.IsAssignableFrom<IChunkRefinementStrategy>(strategy);
    }

    [Fact]
    public async Task RefineAsync_EmptyInput_ProducesNoChunks()
    {
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f]),
            new SemanticChunkingOptions());

        var result = await strategy.RefineAsync(ToAsync([])).ToListAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task RefineAsync_ShortChunk_PassesThroughUnchanged()
    {
        // Chunk shorter than MinChunkSize must not be sub-split
        var opts = new SemanticChunkingOptions { MinChunkSize = 1000 };
        var strategy = new SemanticChunkingStrategy(MockEmbedder([1f, 0f]), opts);

        var chunk = new TextChunk
        {
            Text = "Short text.",
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = 11,
        };

        var result = await strategy.RefineAsync(ToAsync([chunk])).ToListAsync();

        Assert.Single(result);
        Assert.Equal("Short text.", result[0].Text);
    }

    [Fact]
    public async Task RefineAsync_LongChunk_SubSplitsAtSentenceBoundaries()
    {
        // Chunk text longer than MinChunkSize; embedder returns alternating vectors
        // so sentence-level split produces multiple chunks.
        var opts = new SemanticChunkingOptions { MinChunkSize = 5 };
        var strategy = new SemanticChunkingStrategy(
            MockEmbedder([1f, 0f], [0f, 1f], [1f, 0f]),
            opts);

        // Build text that will split into 3 sentences and exceed MinChunkSize
        var text = "First sentence here. Second sentence there. Third sentence ok.";
        var chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId("doc1"),
            ChunkIndex = 0,
            StartPosition = 0,
            EndPosition = text.Length,
        };

        var result = await strategy.RefineAsync(ToAsync([chunk])).ToListAsync();

        // Must produce more than 1 chunk when text is sub-split
        Assert.True(result.Count > 1);
        Assert.All(result, c => Assert.Equal("doc1", c.DocumentId));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SemanticRefinementStrategyTests" 2>&1 | tail -5
```
Expected: compile error — `RefineAsync` does not exist yet

**Step 3: Implement `RefineAsync` on `SemanticChunkingStrategy`**

Add `IChunkRefinementStrategy` to the class declaration:

```csharp
public sealed partial class SemanticChunkingStrategy(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    SemanticChunkingOptions options) : IChunkingStrategy, IDocumentChunkingStrategy, IChunkRefinementStrategy
```

Add the `RefineAsync` method after `ChunkDocumentAsync`:

```csharp
public async IAsyncEnumerable<TextChunk> RefineAsync(
    IAsyncEnumerable<TextChunk> chunks,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    int chunkIndex = 0;
    await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
    {
        if (chunk.Text.Length <= _options.MinChunkSize)
        {
            yield return chunk with { ChunkIndex = chunkIndex++ };
            continue;
        }

        // Treat the oversized chunk text as a single-section document and re-split at
        // sentence boundaries using the existing per-section path.
        var syntheticSection = new DocumentSection { DocumentId = chunk.DocumentId, Text = chunk.Text };

        await foreach (var sub in ChunkAsync(syntheticSection, new ChunkingOptions(), cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return sub with { ChunkIndex = chunkIndex++ };
        }
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "SemanticRefinementStrategyTests"
```
Expected: all 4 tests pass

**Step 5: Run full suite**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: all tests pass

**Step 6: Commit**

```bash
git add src/Rag.NET/Chunking/SemanticChunkingStrategy.cs
git add tests/Rag.NET.Tests/Chunking/SemanticRefinementStrategyTests.cs
git commit -m "feat: implement SemanticChunkingStrategy.RefineAsync (IChunkRefinementStrategy)"
```

---

### Task 4: `ParseBehavior` optional refinement pass

**Files:**
- Modify: `src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs`
- Create: `tests/Rag.NET.Tests/Ingestion/Behaviors/ParseBehaviorRefinementTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/Ingestion/Behaviors/ParseBehaviorRefinementTests.cs`:

```csharp
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class ParseBehaviorRefinementTests
{
    private static IDocumentParser MakeParser(params string[] sectionTexts)
    {
        var parser = Substitute.For<IDocumentParser>();
        parser.CanParse(Arg.Any<string>()).Returns(true);
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                async IAsyncEnumerable<DocumentSection> Yield()
                {
                    foreach (var text in sectionTexts)
                        yield return new DocumentSection { DocumentId = "doc1", Text = text };
                }
                return Yield();
            });
        return parser;
    }

    private static IChunkingStrategy MakeChunker(params TextChunk[] chunks)
    {
        var chunker = Substitute.For<IChunkingStrategy>();
        chunker.ChunkAsync(Arg.Any<DocumentSection>(), Arg.Any<ChunkingOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                async IAsyncEnumerable<TextChunk> Yield()
                {
                    foreach (var c in chunks) yield return c;
                }
                return Yield();
            });
        return chunker;
    }

    private static TextChunk MakeChunk(string text) =>
        new() { Text = text, DocumentId = new DocumentId("doc1"), ChunkIndex = 0, StartPosition = 0, EndPosition = text.Length };

    [Fact]
    public async Task HandleAsync_WhenRefinementStrategyRegistered_RefineAsyncIsCalled()
    {
        var refinedChunk = MakeChunk("refined");
        var refinement = Substitute.For<IChunkRefinementStrategy>();
        refinement.RefineAsync(Arg.Any<IAsyncEnumerable<TextChunk>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                async IAsyncEnumerable<TextChunk> Yield()
                {
                    yield return refinedChunk;
                }
                return Yield();
            });

        var behavior = new ParseBehavior
        {
            Parsers = [MakeParser("Some text.")],
            ChunkingStrategy = MakeChunker(MakeChunk("original")),
            ChunkingOptions = new ChunkingOptions(),
            RefinementStrategy = refinement,
        };

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc1") },
            GetNextBm25DocId = () => 0,
        };
        await behavior.HandleAsync(ctx, CancellationToken.None,
            (_, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Single(ctx.Chunks);
        Assert.Equal("refined", ctx.Chunks[0].Text);
    }

    [Fact]
    public async Task HandleAsync_WhenNoRefinementStrategy_ChunksPassThroughUnchanged()
    {
        var original = MakeChunk("original");

        var behavior = new ParseBehavior
        {
            Parsers = [MakeParser("Some text.")],
            ChunkingStrategy = MakeChunker(original),
            ChunkingOptions = new ChunkingOptions(),
            // RefinementStrategy intentionally not set (null)
        };

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc1") },
            GetNextBm25DocId = () => 0,
        };
        await behavior.HandleAsync(ctx, CancellationToken.None,
            (_, _) => ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.Single(ctx.Chunks);
        Assert.Equal("original", ctx.Chunks[0].Text);
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "ParseBehaviorRefinementTests" 2>&1 | tail -5
```
Expected: compile error — `RefinementStrategy` property does not exist yet

**Step 3: Update `ParseBehavior`**

In [ParseBehavior.cs](src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs), add the optional inject property and the refinement pass.

Add property after `ChunkingOptions`:
```csharp
[Inject] public IChunkRefinementStrategy? RefinementStrategy { get; set; }
```

Replace the `HandleAsync` body to add the refinement pass between chunking and the progress report:

```csharp
public async ValueTask<IngestionResult> HandleAsync(
    IngestionContext ctx, CancellationToken ct,
    Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
{
    var parser = Parsers.FirstOrDefault(p => p.CanParse(ctx.Metadata.ContentType ?? "text/plain"))
        ?? throw new NoParserFoundException(ctx.Metadata.ContentType ?? "text/plain");

    if (ChunkingStrategy is IDocumentChunkingStrategy docStrategy)
        await ChunkDocumentAsync(ctx, parser, docStrategy, ct).ConfigureAwait(false);
    else
        await ChunkPerSectionAsync(ctx, parser, ct).ConfigureAwait(false);

    if (RefinementStrategy is not null)
        await ApplyRefinementAsync(ctx, ct).ConfigureAwait(false);

    ctx.Progress?.Report(new()
    {
        Stage = IngestionProgressStage.Parsing,
        DocumentId = ctx.Metadata.DocumentId,
        Message = "Parsing complete",
    });

    return await next(ctx, ct).ConfigureAwait(false);
}
```

Add the `ApplyRefinementAsync` private helper at the bottom of the class (before the closing brace):

```csharp
private async Task ApplyRefinementAsync(IngestionContext ctx, CancellationToken ct)
{
    var raw = ctx.Chunks.ToAsyncEnumerable();
    var refined = new List<TextChunk>();
    await foreach (var chunk in RefinementStrategy!.RefineAsync(raw, ct).ConfigureAwait(false))
        refined.Add(chunk);
    ctx.Chunks.Clear();
    ctx.Chunks.AddRange(refined);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "ParseBehaviorRefinementTests"
```
Expected: 2 tests pass

**Step 5: Run full suite**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: all tests pass

**Step 6: Commit**

```bash
git add src/Rag.NET/Ingestion/Behaviors/ParseBehavior.cs
git add tests/Rag.NET.Tests/Ingestion/Behaviors/ParseBehaviorRefinementTests.cs
git commit -m "feat: add optional IChunkRefinementStrategy pass to ParseBehavior"
```

---

### Task 5: `RagBuilder` — update `UseSemanticChunking`, add `UseSemanticRefinement`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseSemanticChunkingTests.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseSemanticRefinementTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Tests/DependencyInjection/UseSemanticChunkingTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseSemanticChunkingTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        return services;
    }

    [Fact]
    public void UseSemanticChunking_RegistersIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkingStrategy>());
    }

    [Fact]
    public void UseSemanticChunking_RegistersIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IDocumentChunkingStrategy>());
    }

    [Fact]
    public void UseSemanticChunking_RegistersIChunkRefinementStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkRefinementStrategy>());
    }

    [Fact]
    public void UseSemanticChunking_AllInterfacesResolveToDifferentInstances_SameUnderlyingType()
    {
        // Registrations are singletons — all three must be the SAME instance
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticChunking()).BuildServiceProvider();
        var chunking = sp.GetRequiredService<IChunkingStrategy>();
        var docChunking = sp.GetRequiredService<IDocumentChunkingStrategy>();
        var refinement = sp.GetRequiredService<IChunkRefinementStrategy>();

        Assert.Same(chunking, docChunking);
        Assert.Same(chunking, refinement);
    }
}
```

Create `tests/Rag.NET.Tests/DependencyInjection/UseSemanticRefinementTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Chunking;
using Rag.NET.DependencyInjection;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseSemanticRefinementTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        return services;
    }

    [Fact]
    public void UseSemanticRefinement_RegistersIChunkRefinementStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticRefinement()).BuildServiceProvider();
        Assert.IsType<SemanticChunkingStrategy>(sp.GetRequiredService<IChunkRefinementStrategy>());
    }

    [Fact]
    public void UseSemanticRefinement_DoesNotRegisterIChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticRefinement()).BuildServiceProvider();
        Assert.Null(sp.GetService<IChunkingStrategy>());
    }

    [Fact]
    public void UseSemanticRefinement_DoesNotRegisterIDocumentChunkingStrategy()
    {
        var sp = BaseServices().AddRagNet(rag => rag.UseSemanticRefinement()).BuildServiceProvider();
        Assert.Null(sp.GetService<IDocumentChunkingStrategy>());
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "UseSemanticChunkingTests|UseSemanticRefinementTests" 2>&1 | tail -10
```
Expected: failures — `IDocumentChunkingStrategy`, `IChunkRefinementStrategy` not registered by `UseSemanticChunking`; `UseSemanticRefinement` method does not exist

**Step 3: Update `RagBuilder`**

Replace `UseSemanticChunking` and add `UseSemanticRefinement` in [RagBuilder.cs](src/Rag.NET/DependencyInjection/RagBuilder.cs).

Replace:
```csharp
public RagBuilder UseSemanticChunking(SemanticChunkingOptions? options = null)
{
    Services.AddSingleton(options ?? new SemanticChunkingOptions());
    Services.AddSingleton<IChunkingStrategy, SemanticChunkingStrategy>();
    return this;
}
```

With:
```csharp
/// <summary>
/// Registers <see cref="SemanticChunkingStrategy"/> as <see cref="IChunkingStrategy"/>,
/// <see cref="IDocumentChunkingStrategy"/>, and <see cref="IChunkRefinementStrategy"/>.
/// All three interfaces resolve to the same singleton instance.
/// Uses the same <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> registered for retrieval
/// by default. Override via <see cref="SemanticChunkingOptions.ChunkingEmbedder"/> for a
/// smaller/faster model at chunking time.
/// </summary>
public RagBuilder UseSemanticChunking(SemanticChunkingOptions? options = null)
{
    Services.AddSingleton(options ?? new SemanticChunkingOptions());
    Services.AddSingleton<SemanticChunkingStrategy>();
    Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
    Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
    Services.AddSingleton<IChunkRefinementStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
    return this;
}

/// <summary>
/// Registers <see cref="SemanticChunkingStrategy"/> as only <see cref="IChunkRefinementStrategy"/>.
/// Use with <c>UseHierarchicalMerging()</c> to add semantic sub-splitting to a hierarchical pipeline
/// without replacing the primary chunking strategy.
/// </summary>
public RagBuilder UseSemanticRefinement(SemanticChunkingOptions? options = null)
{
    Services.AddSingleton(options ?? new SemanticChunkingOptions());
    Services.AddSingleton<SemanticChunkingStrategy>();
    Services.AddSingleton<IChunkRefinementStrategy>(sp => sp.GetRequiredService<SemanticChunkingStrategy>());
    return this;
}
```

**Step 4: Run DI tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Tests/ --filter "UseSemanticChunkingTests|UseSemanticRefinementTests"
```
Expected: all 7 tests pass

**Step 5: Run full suite**

```bash
dotnet test tests/Rag.NET.Tests/
```
Expected: all tests pass

**Step 6: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs
git add tests/Rag.NET.Tests/DependencyInjection/UseSemanticChunkingTests.cs
git add tests/Rag.NET.Tests/DependencyInjection/UseSemanticRefinementTests.cs
git commit -m "feat: update UseSemanticChunking to register all 3 interfaces; add UseSemanticRefinement"
```

---

## Implementation Notes

- `SemanticChunkingOptions` requires `SemanticChunkingStrategy` constructor parameter — `UseSemanticRefinement` must also register `SemanticChunkingOptions`.
- `DocumentSection` needs no new properties — `RefineAsync` creates a synthetic one with `DocumentId` from the incoming chunk.
- `ParseBehavior` uses `ToAsyncEnumerable()` (from `System.Linq.Async` / `Microsoft.Bcl.AsyncInterfaces`). Check existing usages in `ParseBehavior.cs` — `ctx.Sections.ToAsyncEnumerable()` is already used in `ChunkDocumentAsync`. Same pattern for `ApplyRefinementAsync`.
- `[Inject]` on a nullable property: ZeroAlloc.Inject skips injection when the service is not registered. Verify by checking `ZeroAlloc.Inject` docs or grep existing code for nullable `[Inject]` usage. If not supported, fall back to constructor-injection with `IServiceProvider.GetService<T>()` resolved inside `HandleAsync`.
