# RAPTOR Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `Rag.NET.Raptor` NuGet package that builds hierarchical summary trees at ingestion time and optionally boosts/filters summary chunks at retrieval time.

**Architecture:** Separate package referencing `Rag.NET` core. Two pipeline behaviors: `RaptorIngestionBehavior` (IIngestionBehavior — positioned after EmbeddingBehavior, before StorageBehavior) and `RaptorRetrievalBehavior` (IRetrievalBehavior — positioned before RerankingBehavior). Math layer uses MathNet.Numerics for GMM+BIC and a vendored UMAP implementation for dimensionality reduction. DI extension `UseRaptor()` on `RagBuilder`.

**Tech Stack:** .NET 10, MathNet.Numerics, Microsoft.Extensions.AI abstractions, xunit.v3, NSubstitute

---

### Task 1: Create project scaffolding

**Files:**
- Create: `src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`
- Modify: `Rag.NET.slnx` — add src + test projects

**Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Raptor</RootNamespace>
    <PackageId>Rag.NET.Raptor</PackageId>
    <Description>RAPTOR — Recursive Abstractive Processing for Tree-Organized Retrieval for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="MathNet.Numerics" Version="6.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create the test project file**

Create `tests/Rag.NET.Raptor.Tests/Rag.NET.Raptor.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Raptor\Rag.NET.Raptor.csproj" />
    <ProjectReference Include="..\..\src\Rag.NET\Rag.NET.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit.v3" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>
    <PackageReference Include="NSubstitute" Version="5.*" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
  </ItemGroup>

</Project>
```

**Step 3: Add both projects to the solution**

Add to `Rag.NET.slnx` inside `/src/` folder:
```xml
<Project Path="src/Rag.NET.Raptor/Rag.NET.Raptor.csproj" />
```
Add to `/tests/` folder:
```xml
<Project Path="tests/Rag.NET.Raptor.Tests/Rag.NET.Raptor.Tests.csproj" />
```

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/ tests/Rag.NET.Raptor.Tests/ Rag.NET.slnx
git commit -m "feat(raptor): add project scaffolding for Rag.NET.Raptor"
```

---

### Task 2: UMAP dimensionality reduction

**Files:**
- Create: `src/Rag.NET.Raptor/Math/Umap.cs`
- Test: `tests/Rag.NET.Raptor.Tests/Math/UmapTests.cs`

This is a minimal UMAP implementation for clustering purposes only (not visualization). It reduces high-dimensional embeddings to a low-dimensional space suitable for GMM clustering.

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Raptor.Tests/Math/UmapTests.cs`:

```csharp
using Rag.NET.Raptor.Math;
using Xunit;

namespace Rag.NET.Raptor.Tests.Math;

public class UmapTests
{
    [Fact]
    public void Fit_ReducesDimensionality()
    {
        // 10 points in 50 dimensions
        var data = CreateRandomData(10, 50, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 3);

        Assert.Equal(10, result.Length);
        Assert.Equal(3, result[0].Length);
    }

    [Fact]
    public void Fit_PreservesRelativeDistances()
    {
        // Create two tight clusters in high-D space
        var cluster1 = CreateCluster(center: 0f, count: 5, dims: 50, seed: 1);
        var cluster2 = CreateCluster(center: 10f, count: 5, dims: 50, seed: 2);
        var data = cluster1.Concat(cluster2).ToArray();

        var result = Umap.Fit(data, targetDimensions: 3);

        // Intra-cluster distances should be smaller than inter-cluster
        var intra1 = EuclideanDistance(result[0], result[1]);
        var inter = EuclideanDistance(result[0], result[5]);
        Assert.True(intra1 < inter, "Intra-cluster distance should be less than inter-cluster distance");
    }

    [Fact]
    public void Fit_WithFewerPointsThanDimensions_DoesNotThrow()
    {
        var data = CreateRandomData(3, 50, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 2);

        Assert.Equal(3, result.Length);
        Assert.Equal(2, result[0].Length);
    }

    [Fact]
    public void Fit_TargetDimensionsEqualToInput_ReturnsOriginalShape()
    {
        var data = CreateRandomData(5, 3, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 3);

        Assert.Equal(5, result.Length);
        Assert.Equal(3, result[0].Length);
    }

    private static float[][] CreateRandomData(int count, int dims, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray())
            .ToArray();
    }

    private static float[][] CreateCluster(float center, int count, int dims, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(0, dims).Select(_ => center + (float)(rng.NextDouble() * 0.1)).ToArray())
            .ToArray();
    }

    private static double EuclideanDistance(float[] a, float[] b)
        => System.Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~UmapTests" -v q`
Expected: FAIL — `Umap` type does not exist

**Step 3: Implement Umap**

Create `src/Rag.NET.Raptor/Math/Umap.cs`:

Implement a simplified UMAP using these steps:
1. Build k-nearest-neighbor graph (k=15) using Euclidean distance
2. Compute fuzzy simplicial set (symmetrize KNN graph with local connectivity)
3. Initialize low-dimensional embedding via PCA (spectral init)
4. Optimize embedding with SGD using cross-entropy loss on graph edges

Key parameters: `nNeighbors = 15`, `minDist = 0.1f`, `nEpochs = 200`.

The class should be `internal static` with a single public method:
```csharp
internal static class Umap
{
    internal static float[][] Fit(float[][] data, int targetDimensions, int nNeighbors = 15, float minDist = 0.1f, int nEpochs = 200)
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~UmapTests" -v q`
Expected: PASS (all 4 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/Math/Umap.cs tests/Rag.NET.Raptor.Tests/Math/UmapTests.cs
git commit -m "feat(raptor): add UMAP dimensionality reduction"
```

---

### Task 3: GMM clustering with BIC model selection

**Files:**
- Create: `src/Rag.NET.Raptor/Math/GaussianMixtureModel.cs`
- Test: `tests/Rag.NET.Raptor.Tests/Math/GaussianMixtureModelTests.cs`

Uses MathNet.Numerics for matrix operations. Implements Expectation-Maximization with diagonal covariance (full covariance is overkill for clustering reduced embeddings).

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Raptor.Tests/Math/GaussianMixtureModelTests.cs`:

```csharp
using Rag.NET.Raptor.Math;
using Xunit;

namespace Rag.NET.Raptor.Tests.Math;

public class GaussianMixtureModelTests
{
    [Fact]
    public void Fit_TwoClusters_AssignsPointsCorrectly()
    {
        var cluster1 = CreateCluster(center: [0f, 0f], count: 20, spread: 0.1f, seed: 1);
        var cluster2 = CreateCluster(center: [10f, 10f], count: 20, spread: 0.1f, seed: 2);
        var data = cluster1.Concat(cluster2).ToArray();

        var result = GaussianMixtureModel.Fit(data, k: 2);

        Assert.Equal(40, result.Assignments.Length);
        // All points in cluster1 should share the same label
        var label1 = result.Assignments[0];
        Assert.All(result.Assignments.Take(20), a => Assert.Equal(label1, a));
        // All points in cluster2 should share a different label
        var label2 = result.Assignments[20];
        Assert.NotEqual(label1, label2);
        Assert.All(result.Assignments.Skip(20), a => Assert.Equal(label2, a));
    }

    [Fact]
    public void Fit_ReturnsResponsibilitiesWithSoftAssignment()
    {
        var cluster1 = CreateCluster(center: [0f, 0f], count: 10, spread: 0.1f, seed: 1);
        var cluster2 = CreateCluster(center: [10f, 10f], count: 10, spread: 0.1f, seed: 2);
        var data = cluster1.Concat(cluster2).ToArray();

        var result = GaussianMixtureModel.Fit(data, k: 2);

        // Responsibilities should be shape [n, k]
        Assert.Equal(20, result.Responsibilities.Length);
        Assert.Equal(2, result.Responsibilities[0].Length);
        // Each row should sum to ~1
        for (int i = 0; i < result.Responsibilities.Length; i++)
        {
            var sum = result.Responsibilities[i].Sum();
            Assert.InRange(sum, 0.99, 1.01);
        }
    }

    [Fact]
    public void SelectK_WithBic_FindsOptimalClusterCount()
    {
        var cluster1 = CreateCluster(center: [0f, 0f], count: 30, spread: 0.3f, seed: 1);
        var cluster2 = CreateCluster(center: [10f, 10f], count: 30, spread: 0.3f, seed: 2);
        var cluster3 = CreateCluster(center: [20f, 0f], count: 30, spread: 0.3f, seed: 3);
        var data = cluster1.Concat(cluster2).Concat(cluster3).ToArray();

        var optimalK = GaussianMixtureModel.SelectK(data, maxK: 6);

        Assert.InRange(optimalK, 2, 4); // BIC should find 3 as optimal, allow some tolerance
    }

    [Fact]
    public void Fit_SingleCluster_AssignsAllToSameLabel()
    {
        var data = CreateCluster(center: [5f, 5f], count: 20, spread: 0.5f, seed: 42);

        var result = GaussianMixtureModel.Fit(data, k: 1);

        Assert.All(result.Assignments, a => Assert.Equal(0, a));
    }

    private static float[][] CreateCluster(float[] center, int count, float spread, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => center.Select(c => c + (float)(rng.NextDouble() - 0.5) * spread * 2).ToArray())
            .ToArray();
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~GaussianMixtureModelTests" -v q`
Expected: FAIL — type does not exist

**Step 3: Implement GaussianMixtureModel**

Create `src/Rag.NET.Raptor/Math/GaussianMixtureModel.cs`:

```csharp
namespace Rag.NET.Raptor.Math;

internal readonly record struct GmmResult(int[] Assignments, float[][] Responsibilities);

internal static class GaussianMixtureModel
{
    /// <summary>Fit a GMM with k components using EM with diagonal covariance.</summary>
    internal static GmmResult Fit(float[][] data, int k, int maxIterations = 100, double tolerance = 1e-6)
    { /* EM implementation */ }

    /// <summary>Select optimal k via BIC, testing k=1..maxK.</summary>
    internal static int SelectK(float[][] data, int maxK, int maxIterations = 100)
    { /* Run Fit for each k, compute BIC = -2*logLikelihood + numParams*ln(n), return argmin */ }
}
```

EM algorithm:
1. Initialize means via k-means++ seeding
2. E-step: compute responsibilities (posterior probabilities)
3. M-step: update means, diagonal variances, mixing weights
4. Repeat until convergence (log-likelihood change < tolerance)

BIC = -2 * logLikelihood + numParams * ln(n), where numParams for diagonal GMM = k * (2*d + 1) - 1.

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~GaussianMixtureModelTests" -v q`
Expected: PASS (all 4 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/Math/GaussianMixtureModel.cs tests/Rag.NET.Raptor.Tests/Math/GaussianMixtureModelTests.cs
git commit -m "feat(raptor): add GMM clustering with BIC model selection"
```

---

### Task 4: RaptorOptions configuration model

**Files:**
- Create: `src/Rag.NET.Raptor/RaptorOptions.cs`
- Create: `src/Rag.NET.Raptor/RaptorRetrievalOptions.cs`
- Create: `src/Rag.NET.Raptor/RaptorRetrievalMode.cs`

**Step 1: Create RaptorRetrievalMode enum**

Create `src/Rag.NET.Raptor/RaptorRetrievalMode.cs`:

```csharp
namespace Rag.NET.Raptor;

/// <summary>Controls how RAPTOR summary chunks participate in retrieval scoring.</summary>
public enum RaptorRetrievalMode
{
    /// <summary>All levels participate via vector similarity naturally. No score adjustment.</summary>
    Blend,

    /// <summary>Multiply scores of summary chunks (raptor_level &gt; 0) by SummaryBoostFactor.</summary>
    Boost,

    /// <summary>Restrict results to specific RAPTOR tree levels via MinRaptorLevel / MaxRaptorLevel.</summary>
    Filter,
}
```

**Step 2: Create RaptorOptions**

Create `src/Rag.NET.Raptor/RaptorOptions.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Raptor;

/// <summary>Configuration for the RAPTOR ingestion behavior.</summary>
public sealed class RaptorOptions
{
    /// <summary>Toggle RAPTOR tree building on/off. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skip RAPTOR if the document has fewer embedded chunks than this. Default: 5.</summary>
    public int MinChunksForRaptor { get; set; } = 5;

    /// <summary>UMAP target dimensionality for clustering. Default: 10.</summary>
    public int ReducedDimensionality { get; set; } = 10;

    /// <summary>Cap for GMM cluster count. Null = BIC auto-selects. Default: null.</summary>
    public int? MaxClusters { get; set; }

    /// <summary>Cap recursion depth. Null = recurse until 1 cluster remains. Default: null.</summary>
    public int? MaxTreeDepth { get; set; }

    /// <summary>Keep original leaf chunks alongside summaries. Default: true.</summary>
    public bool StoreLeafChunks { get; set; } = true;

    /// <summary>LLM prompt template for cluster summarization. {chunks} is replaced with concatenated text.</summary>
    public string SummaryPrompt { get; set; } = """
        You are a summarization assistant. Below are several related text passages from the same document cluster.
        Write a concise, comprehensive summary that captures all key information.

        Passages:
        {chunks}

        Summary:
        """;

    /// <summary>Optional separate chat client for summaries (e.g. a cheaper model). Null = use DI-registered IChatClient.</summary>
    public IChatClient? SummaryChatClient { get; set; }

    /// <summary>Optional separate embedder for summaries. Null = use DI-registered IEmbeddingGenerator.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? SummaryEmbedder { get; set; }
}
```

**Step 3: Create RaptorRetrievalOptions**

Create `src/Rag.NET.Raptor/RaptorRetrievalOptions.cs`:

```csharp
namespace Rag.NET.Raptor;

/// <summary>Configuration for the RAPTOR retrieval behavior.</summary>
public sealed class RaptorRetrievalOptions
{
    /// <summary>Retrieval mode. Default: Blend.</summary>
    public RaptorRetrievalMode Mode { get; set; } = RaptorRetrievalMode.Blend;

    /// <summary>Score multiplier for summary chunks in Boost mode. Default: 1.2.</summary>
    public double SummaryBoostFactor { get; set; } = 1.2;

    /// <summary>Minimum RAPTOR level to include in Filter mode. Null = no lower bound.</summary>
    public int? MinRaptorLevel { get; set; }

    /// <summary>Maximum RAPTOR level to include in Filter mode. Null = no upper bound.</summary>
    public int? MaxRaptorLevel { get; set; }
}
```

**Step 4: Verify build**

Run: `dotnet build src/Rag.NET.Raptor/Rag.NET.Raptor.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/RaptorOptions.cs src/Rag.NET.Raptor/RaptorRetrievalOptions.cs src/Rag.NET.Raptor/RaptorRetrievalMode.cs
git commit -m "feat(raptor): add configuration models"
```

---

### Task 5: RaptorIngestionBehavior

**Files:**
- Create: `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorIngestionBehaviorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    [Fact]
    public async Task HandleAsync_WhenDisabled_CallsNextWithoutModification()
    {
        var options = new RaptorOptions { Enabled = false };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 10);
        var originalCount = ctx.EmbeddedChunks.Count;
        var nextCalled = false;

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }); });

        Assert.True(nextCalled);
        Assert.Equal(originalCount, ctx.EmbeddedChunks.Count);
    }

    [Fact]
    public async Task HandleAsync_BelowMinChunks_SkipsRaptor()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 10 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 5);
        var originalCount = ctx.EmbeddedChunks.Count;

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        Assert.Equal(originalCount, ctx.EmbeddedChunks.Count);
    }

    [Fact]
    public async Task HandleAsync_AddsSummaryChunksWithRaptorMetadata()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary of cluster");
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var summaryChunks = ctx.EmbeddedChunks.Where(ec => ec.Chunk.Metadata.ContainsKey("raptor_level")).ToList();
        Assert.NotEmpty(summaryChunks);
        Assert.All(summaryChunks, sc =>
        {
            Assert.Equal("1", sc.Chunk.Metadata["raptor_level"]);
            Assert.True(sc.Chunk.Metadata.ContainsKey("raptor_cluster_id"));
            Assert.True(sc.Chunk.Metadata.ContainsKey("raptor_child_ids"));
        });
    }

    [Fact]
    public async Task HandleAsync_StoreLeafChunksFalse_RemovesOriginals()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1, StoreLeafChunks = false };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        SetupChatClient("Summary");
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        // Only summary chunks should remain — no leaf chunks (raptor_level=0 or no raptor_level)
        Assert.All(ctx.EmbeddedChunks, ec => Assert.True(ec.Chunk.Metadata.ContainsKey("raptor_level")));
    }

    [Fact]
    public async Task HandleAsync_RespectsMaxTreeDepth()
    {
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 2 };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 20, embeddingDims: 8);

        SetupChatClient("Summary");
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        var maxLevel = ctx.EmbeddedChunks
            .Where(ec => ec.Chunk.Metadata.ContainsKey("raptor_level"))
            .Select(ec => int.Parse(ec.Chunk.Metadata["raptor_level"]))
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(maxLevel <= 2);
    }

    [Fact]
    public async Task HandleAsync_UsesSummaryChatClientWhenProvided()
    {
        var customClient = Substitute.For<IChatClient>();
        var options = new RaptorOptions { MinChunksForRaptor = 2, ReducedDimensionality = 2, MaxTreeDepth = 1, SummaryChatClient = customClient };
        var sut = new RaptorIngestionBehavior(_chatClient, _embedder, options);
        var ctx = CreateContext(chunkCount: 6, embeddingDims: 8);

        customClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Custom summary")]));
        SetupEmbedder(8);

        await sut.HandleAsync(ctx, CancellationToken.None,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = c.EmbeddedChunks.Count }));

        await customClient.Received().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        await _chatClient.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    private IngestionContext CreateContext(int chunkCount, int embeddingDims = 8)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("test-doc"), ContentType = "text/plain" },
            GetNextBm25DocId = () => 0,
        };

        var rng = new Random(42);
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = new TextChunk
            {
                Text = $"Chunk {i} content about topic {i % 3}",
                DocumentId = new DocumentId("test-doc"),
                ChunkIndex = i,
            };
            var embedding = Enumerable.Range(0, embeddingDims).Select(_ => (float)rng.NextDouble()).ToArray();
            ctx.EmbeddedChunks.Add(new EmbeddedChunk { Chunk = chunk, Embedding = new ReadOnlyMemory<float>(embedding) });
        }

        return ctx;
    }

    private void SetupChatClient(string response)
    {
        _chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
    }

    private void SetupEmbedder(int dims)
    {
        _embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var texts = callInfo.Arg<IEnumerable<string>>().ToList();
                var rng = new Random(123);
                return Task.FromResult<GeneratedEmbeddings<Embedding<float>>>(
                    new(texts.Select(_ => new Embedding<float>(
                        Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray())).ToList()));
            });
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~RaptorIngestionBehaviorTests" -v q`
Expected: FAIL — `RaptorIngestionBehavior` does not exist

**Step 3: Implement RaptorIngestionBehavior**

Create `src/Rag.NET.Raptor/RaptorIngestionBehavior.cs`:

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Raptor.Math;

namespace Rag.NET.Raptor;

/// <summary>
/// Ingestion behavior that builds a RAPTOR tree of recursive summaries.
/// Position: after EmbeddingBehavior, before StorageBehavior.
/// </summary>
public sealed class RaptorIngestionBehavior(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    RaptorOptions options) : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (!options.Enabled || ctx.EmbeddedChunks.Count < options.MinChunksForRaptor)
            return await next(ctx, ct).ConfigureAwait(false);

        var leafChunks = ctx.EmbeddedChunks.ToList();
        var currentLevel = leafChunks;
        var allSummaries = new List<EmbeddedChunk>();
        var level = 0;

        while (currentLevel.Count > 1 && (options.MaxTreeDepth is null || level < options.MaxTreeDepth))
        {
            level++;
            var embeddings = currentLevel.Select(ec => ec.Embedding.ToArray()).ToArray();

            // UMAP reduce
            var targetDims = System.Math.Min(options.ReducedDimensionality, embeddings[0].Length);
            var reduced = embeddings[0].Length > targetDims
                ? Umap.Fit(embeddings, targetDims)
                : embeddings;

            // GMM cluster
            var k = options.MaxClusters.HasValue
                ? System.Math.Min(options.MaxClusters.Value, currentLevel.Count)
                : GaussianMixtureModel.SelectK(reduced, maxK: System.Math.Min(currentLevel.Count, 10));

            if (k <= 1) break;

            var gmm = GaussianMixtureModel.Fit(reduced, k);

            // Group chunks by assignment
            var clusters = currentLevel
                .Select((ec, i) => (ec, cluster: gmm.Assignments[i]))
                .GroupBy(x => x.cluster)
                .ToList();

            var client = options.SummaryChatClient ?? chatClient;
            var emb = options.SummaryEmbedder ?? embedder;
            var summaryChunks = new List<EmbeddedChunk>();

            foreach (var cluster in clusters)
            {
                var childChunks = cluster.Select(x => x.ec).ToList();
                var concatenated = string.Join("\n\n---\n\n", childChunks.Select(c => c.Chunk.Text));
                var prompt = options.SummaryPrompt.Replace("{chunks}", concatenated, StringComparison.Ordinal);

                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
                var summaryText = response.Text ?? string.Empty;

                var summaryEmbeddings = await emb.GenerateAsync(
                    [summaryText], cancellationToken: ct).ConfigureAwait(false);

                var childIds = string.Join(",", childChunks.Select(c => c.Chunk.ChunkIndex));
                var summaryChunk = new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = summaryText,
                        DocumentId = ctx.Metadata.DocumentId,
                        ChunkIndex = ctx.EmbeddedChunks.Count + summaryChunks.Count,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["raptor_level"] = level.ToString(),
                            ["raptor_cluster_id"] = cluster.Key.ToString(),
                            ["raptor_child_ids"] = childIds,
                        },
                    },
                    Embedding = summaryEmbeddings[0].Vector,
                };
                summaryChunks.Add(summaryChunk);
            }

            allSummaries.AddRange(summaryChunks);
            currentLevel = summaryChunks;
        }

        if (!options.StoreLeafChunks)
            ctx.EmbeddedChunks.Clear();

        ctx.EmbeddedChunks.AddRange(allSummaries);

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~RaptorIngestionBehaviorTests" -v q`
Expected: PASS (all 6 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/RaptorIngestionBehavior.cs tests/Rag.NET.Raptor.Tests/RaptorIngestionBehaviorTests.cs
git commit -m "feat(raptor): add RaptorIngestionBehavior"
```

---

### Task 6: RaptorRetrievalBehavior

**Files:**
- Create: `src/Rag.NET.Raptor/RaptorRetrievalBehavior.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RaptorRetrievalBehaviorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Raptor.Tests/RaptorRetrievalBehaviorTests.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RaptorRetrievalBehaviorTests
{
    [Fact]
    public async Task HandleAsync_BlendMode_PassesThroughUnmodified()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Blend };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(results, actual);
    }

    [Fact]
    public async Task HandleAsync_BoostMode_MultipliesSummaryScores()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 2.0 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        // Leaf chunk (no raptor_level) should keep original score
        var leaf = actual.First(r => !r.Chunk.Metadata.ContainsKey("raptor_level"));
        Assert.Equal(0.8, leaf.Score);

        // Summary chunk (raptor_level=1) should have boosted score
        var summary = actual.First(r => r.Chunk.Metadata.ContainsKey("raptor_level") && r.Chunk.Metadata["raptor_level"] == "1");
        Assert.Equal(1.4, summary.Score, precision: 5); // 0.7 * 2.0
    }

    [Fact]
    public async Task HandleAsync_FilterMode_RestrictsToLevelRange()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MinRaptorLevel = 1, MaxRaptorLevel = 1 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Single(actual);
        Assert.Equal("1", actual[0].Chunk.Metadata["raptor_level"]);
    }

    [Fact]
    public async Task HandleAsync_FilterMode_MinLevelOnly_IncludesHigherLevels()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Filter, MinRaptorLevel = 1 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        Assert.Equal(2, actual.Count); // level 1 and level 2
        Assert.All(actual, r => Assert.True(r.Chunk.Metadata.ContainsKey("raptor_level")));
    }

    [Fact]
    public async Task HandleAsync_BoostMode_ResultsAreSortedByScore()
    {
        var options = new RaptorRetrievalOptions { Mode = RaptorRetrievalMode.Boost, SummaryBoostFactor = 3.0 };
        var sut = new RaptorRetrievalBehavior(options);
        var results = CreateResults();
        var ctx = CreateContext();

        var actual = await sut.HandleAsync(ctx, CancellationToken.None, (c, ct) => ValueTask.FromResult(results));

        for (int i = 1; i < actual.Count; i++)
            Assert.True(actual[i - 1].Score >= actual[i].Score, "Results should be sorted descending by score");
    }

    private static RetrievalContext CreateContext() => new()
    {
        Query = "test query",
        Options = new RetrievalOptions(),
    };

    private static IReadOnlyList<SearchResult> CreateResults() =>
    [
        new SearchResult
        {
            Chunk = new TextChunk { Text = "leaf content", DocumentId = new DocumentId("doc"), ChunkIndex = 0 },
            Score = 0.8,
        },
        new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = "summary level 1",
                DocumentId = new DocumentId("doc"),
                ChunkIndex = 1,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["raptor_level"] = "1", ["raptor_cluster_id"] = "0", ["raptor_child_ids"] = "0" },
            },
            Score = 0.7,
        },
        new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = "summary level 2",
                DocumentId = new DocumentId("doc"),
                ChunkIndex = 2,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["raptor_level"] = "2", ["raptor_cluster_id"] = "0", ["raptor_child_ids"] = "1" },
            },
            Score = 0.6,
        },
    ];
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~RaptorRetrievalBehaviorTests" -v q`
Expected: FAIL — `RaptorRetrievalBehavior` does not exist

**Step 3: Implement RaptorRetrievalBehavior**

Create `src/Rag.NET.Raptor/RaptorRetrievalBehavior.cs`:

```csharp
using Rag.NET.Models;
using Rag.NET.Retrieval;

namespace Rag.NET.Raptor;

/// <summary>
/// Retrieval behavior that adjusts scoring/filtering based on RAPTOR tree levels.
/// Position: before RerankingBehavior in the retrieval pipeline.
/// </summary>
public sealed class RaptorRetrievalBehavior(RaptorRetrievalOptions options) : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        return options.Mode switch
        {
            RaptorRetrievalMode.Blend => results,
            RaptorRetrievalMode.Boost => ApplyBoost(results),
            RaptorRetrievalMode.Filter => ApplyFilter(results),
            _ => results,
        };
    }

    private IReadOnlyList<SearchResult> ApplyBoost(IReadOnlyList<SearchResult> results)
    {
        return results
            .Select(r =>
            {
                var level = GetRaptorLevel(r);
                return level > 0
                    ? r with { Score = r.Score * options.SummaryBoostFactor }
                    : r;
            })
            .OrderByDescending(r => r.Score)
            .ToList()
            .AsReadOnly();
    }

    private IReadOnlyList<SearchResult> ApplyFilter(IReadOnlyList<SearchResult> results)
    {
        return results
            .Where(r =>
            {
                var level = GetRaptorLevel(r);
                if (options.MinRaptorLevel.HasValue && level < options.MinRaptorLevel.Value)
                    return false;
                if (options.MaxRaptorLevel.HasValue && level > options.MaxRaptorLevel.Value)
                    return false;
                return true;
            })
            .ToList()
            .AsReadOnly();
    }

    private static int GetRaptorLevel(SearchResult r)
        => r.Chunk.Metadata.TryGetValue("raptor_level", out var levelStr) && int.TryParse(levelStr, out var level)
            ? level
            : 0;
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~RaptorRetrievalBehaviorTests" -v q`
Expected: PASS (all 5 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/RaptorRetrievalBehavior.cs tests/Rag.NET.Raptor.Tests/RaptorRetrievalBehaviorTests.cs
git commit -m "feat(raptor): add RaptorRetrievalBehavior with blend/boost/filter modes"
```

---

### Task 7: DI registration — UseRaptor extension method

**Files:**
- Create: `src/Rag.NET.Raptor/RagBuilderExtensions.cs`
- Test: `tests/Rag.NET.Raptor.Tests/RagBuilderExtensionsTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Raptor.Tests/RagBuilderExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Xunit;

namespace Rag.NET.Raptor.Tests;

public class RagBuilderExtensionsTests
{
    [Fact]
    public void UseRaptor_RegistersOptionsAsSingleton()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalOptions));
    }

    [Fact]
    public void UseRaptor_WithConfigure_AppliesOptions()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor(o => o.MinChunksForRaptor = 42);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorOptions>();
        Assert.Equal(42, opts.MinChunksForRaptor);
    }

    [Fact]
    public void UseRaptor_WithRetrievalConfigure_AppliesOptions()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor(retrieval: o => o.Mode = RaptorRetrievalMode.Boost);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<RaptorRetrievalOptions>();
        Assert.Equal(RaptorRetrievalMode.Boost, opts.Mode);
    }

    [Fact]
    public void UseRaptor_RegistersIngestionBehavior()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorIngestionBehavior));
    }

    [Fact]
    public void UseRaptor_RegistersRetrievalBehavior()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.UseRaptor();

        Assert.Contains(services, d => d.ServiceType == typeof(RaptorRetrievalBehavior));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~RagBuilderExtensionsTests" -v q`
Expected: FAIL — `UseRaptor` method does not exist

**Step 3: Implement RagBuilderExtensions**

Create `src/Rag.NET.Raptor/RagBuilderExtensions.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Raptor;

/// <summary>Extension methods for registering RAPTOR in the Rag.NET pipeline.</summary>
public static class RagBuilderExtensions
{
    /// <summary>
    /// Enables RAPTOR — recursive abstractive tree-organized retrieval.
    /// Registers <see cref="RaptorIngestionBehavior"/> and <see cref="RaptorRetrievalBehavior"/>
    /// into the pipeline.
    /// </summary>
    /// <param name="builder">The Rag.NET builder.</param>
    /// <param name="configure">Optional delegate to configure <see cref="RaptorOptions"/>.</param>
    /// <param name="retrieval">Optional delegate to configure <see cref="RaptorRetrievalOptions"/>.</param>
    public static RagBuilder UseRaptor(
        this RagBuilder builder,
        Action<RaptorOptions>? configure = null,
        Action<RaptorRetrievalOptions>? retrieval = null)
    {
        var options = new RaptorOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        var retrievalOptions = new RaptorRetrievalOptions();
        retrieval?.Invoke(retrievalOptions);
        builder.Services.AddSingleton(retrievalOptions);

        builder.Services.AddSingleton<RaptorIngestionBehavior>(sp =>
            new RaptorIngestionBehavior(
                options.SummaryChatClient ?? sp.GetRequiredService<IChatClient>(),
                options.SummaryEmbedder ?? sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                options));

        builder.Services.AddSingleton<RaptorRetrievalBehavior>(sp =>
            new RaptorRetrievalBehavior(sp.GetRequiredService<RaptorRetrievalOptions>()));

        return builder;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ --filter "FullyQualifiedName~RagBuilderExtensionsTests" -v q`
Expected: PASS (all 5 tests)

**Step 5: Commit**

```bash
git add src/Rag.NET.Raptor/RagBuilderExtensions.cs tests/Rag.NET.Raptor.Tests/RagBuilderExtensionsTests.cs
git commit -m "feat(raptor): add UseRaptor DI registration"
```

---

### Task 8: Documentation

**Files:**
- Create: `docs/guide/raptor.md`
- Modify: `docs/reference/features.md` — mark RAPTOR as Done

**Step 1: Create the guide**

Create `docs/guide/raptor.md` with comprehensive content covering:
- What is RAPTOR and why use it
- How it works (tree building, recursive summarization)
- Configuration walkthrough (all options explained)
- Retrieval modes (Blend, Boost, Filter) with when to use each
- Cost/performance trade-offs (LLM calls per cluster per level)
- Integration example with `AddRagNet` + `UseRaptor`
- Pipeline positioning (after EmbeddingBehavior / before RerankingBehavior)
- Troubleshooting tips

**Step 2: Update features reference**

In `docs/reference/features.md`, find the RAPTOR row and change status from "Planned" to "Done".

**Step 3: Commit**

```bash
git add docs/guide/raptor.md docs/reference/features.md
git commit -m "docs: add RAPTOR guide and mark feature as done"
```

---

### Task 9: Build and test full solution

**Step 1: Build the full solution**

Run: `dotnet build Rag.NET.slnx`
Expected: Build succeeded, 0 errors

**Step 2: Run all RAPTOR tests**

Run: `dotnet test tests/Rag.NET.Raptor.Tests/ -v q`
Expected: All tests pass (~20 tests)

**Step 3: Run entire test suite to check for regressions**

Run: `dotnet test Rag.NET.slnx -v q`
Expected: All existing tests still pass

**Step 4: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix(raptor): address build/test issues"
```

---

## Parallel Execution Guide

| Task | Can run in parallel with | Depends on |
|------|--------------------------|------------|
| 1 (scaffolding) | — | — |
| 2 (UMAP) | 3, 4, 6 | 1 |
| 3 (GMM) | 2, 4, 6 | 1 |
| 4 (options) | 2, 3, 6 | 1 |
| 5 (ingestion behavior) | 6 | 1, 2, 3, 4 |
| 6 (retrieval behavior) | 2, 3, 4, 5 | 1, 4 |
| 7 (DI registration) | — | 5, 6 |
| 8 (docs) | 2, 3, 4, 5, 6, 7 | 1 |
| 9 (build + test) | — | all |
