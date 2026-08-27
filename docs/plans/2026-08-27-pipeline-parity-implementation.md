# Pipeline Parity Test — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Assert that a real `AddRagNet` pipeline returns byte-identical retrieval results to the harness's own dense row over the same store, so that a default retrieval behaviour which stops being a no-op fails a test instead of silently invalidating every pinned figure.

**Architecture:** One shared `InMemoryVectorStore` instance is handed to both sides. The harness side calls `AblationRow.Dense.RetrieveAsync`, which takes the store as a parameter and returns `ChunkHit`s. The pipeline side registers that same store instance and the same embedder into a container, calls `AddRagNet()` with no configuration, and runs `IRagPipeline.RetrieveAsync`. Results are projected to `ChunkHit` and compared by id and exact score, in order. Two legs share that one comparison: a fast leg over a synthetic corpus with a deterministic fixture embedder (every push), and a real leg over SciFact with the real ONNX embedder (skips unless provisioned).

**Tech Stack:** .NET 10, C#, xunit.v3 (`Assert.SkipUnless` / `Assert.SkipWhen`, `TestContext.Current.CancellationToken`), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.AI` (`IEmbeddingGenerator<string, Embedding<float>>`).

**Spec:** [`2026-08-27-pipeline-parity-design.md`](2026-08-27-pipeline-parity-design.md)

## Global Constraints

- **Everything lands in `tests/Rag.NET.Benchmarks.Quality.IntegrationTests`.** It already references `Rag.NET` and `Rag.NET.Benchmarks.Quality`, and declares neither `RequiresDocker` nor `RequiresLlm`, so it already runs in the fast tier on every push. **Do not create a new test project.**
- **Do not modify `BeirHarness.cs` or `AblationRow.cs`.** Not behaviour, not visibility. Approach A's entire premise is that the measuring instrument stays fixed. Everything needed is already public or `internal` within this assembly.
- **`Directory.Build.props` sets `TreatWarningsAsErrors=true`.** A warning fails the build.
- **No tolerance on score comparison.** Both sides read the same store, so identical inputs give bit-identical `double`s. Never introduce an epsilon.
- **Commit messages:** conventional commits, header ≤ 100 characters. CI lints every commit a PR adds.
- **The chunk-id shape is `$"{DocumentId.Value}#{ChunkIndex}"`** — copied from `AblationRow.ToChunkHits`, which is `private protected` and therefore not callable. Reproducing this one format string is deliberate and is the only duplication this plan accepts.

---

### Task 1: The fixture embedder and its guard

The fast leg needs an embedder that is deterministic, injective, and **strictly ordering**. This project has already shipped two defects (#332, #333) plus an unbounded-spend infinite loop behind a mock embedder that returned byte-identical vectors, so the contract is asserted by its own test rather than trusted.

The construction is geometric and provable rather than hash-based: text *i* of *n* maps to the 2-D unit vector at angle *i·δ* where *δ = π / (2(n+1))*. All angles lie in *(0, π/2)*, so cosine against the query at angle 0 is **strictly decreasing in *i*** — the expected ranking is `doc-0, doc-1, …` by construction, with no ties possible.

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/OrderingEmbeddingGenerator.cs`
- Test: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/OrderingEmbeddingGeneratorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal sealed class OrderingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>`, constructed as `new OrderingEmbeddingGenerator(IReadOnlyList<string> orderedTexts)`. Exposes `IReadOnlyList<string> OrderedTexts { get; }`. Throws `ArgumentException` from `GenerateAsync` for any text not in `orderedTexts`.

- [ ] **Step 1: Write the failing guard test**

Create `OrderingEmbeddingGeneratorTests.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The guard on <see cref="OrderingEmbeddingGenerator"/>'s contract, kept separate from the parity
/// tests so it fails on its own terms.
/// <para>
/// A degenerate fixture embedder is not a hypothetical here. Phase 6.2.3's mock constructed
/// <c>new Random(123)</c> inside its callback, so every vector came back byte-identical; identical
/// points collapse to one cluster, no test ever built a RAPTOR tree deeper than one level, and
/// #332, #333 and an unbounded-spend infinite loop all stayed unreachable while the suite was
/// green. A degenerate embedder would break the parity test the same way and worse — ties make
/// reordering invisible, so the assertion would pass by construction.
/// </para>
/// </summary>
public sealed class OrderingEmbeddingGeneratorTests
{
    private static readonly string[] Corpus =
        ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"];

    [Fact]
    public async Task GenerateAsync_IsDeterministic()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var first = await generator.GenerateAsync(Corpus, cancellationToken: ct);
        var second = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        for (var i = 0; i < Corpus.Length; i++)
        {
            Assert.Equal(first[i].Vector.ToArray(), second[i].Vector.ToArray());
        }
    }

    [Fact]
    public async Task GenerateAsync_IsInjective()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var vectors = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        for (var i = 0; i < Corpus.Length; i++)
        {
            for (var j = i + 1; j < Corpus.Length; j++)
            {
                Assert.NotEqual(vectors[i].Vector.ToArray(), vectors[j].Vector.ToArray());
            }
        }
    }

    /// <summary>
    /// The property the parity test depends on: cosine against the query is strictly decreasing in
    /// corpus position, so the top-k has exactly one correct order and any reordering or truncation
    /// is observable. Pairwise-distinct is not enough — two documents tying at the same score would
    /// make a swap between them invisible.
    /// </summary>
    [Fact]
    public async Task Similarities_AreStrictlyDecreasing_AndPairwiseDistinct()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        var query = await generator.GenerateAsync(
            [OrderingEmbeddingGenerator.QueryText], cancellationToken: ct);
        var documents = await generator.GenerateAsync(Corpus, cancellationToken: ct);

        var scores = new double[Corpus.Length];
        for (var i = 0; i < Corpus.Length; i++)
        {
            scores[i] = Dot(query[0].Vector.Span, documents[i].Vector.Span);
        }

        for (var i = 1; i < scores.Length; i++)
        {
            Assert.True(
                scores[i] < scores[i - 1],
                $"score[{i}]={scores[i]} is not strictly below score[{i - 1}]={scores[i - 1]}; " +
                "the fixture no longer imposes a unique ordering and the parity assertion would " +
                "pass by construction.");
        }

        Assert.Equal(scores.Length, scores.Distinct().Count());
    }

    [Fact]
    public async Task GenerateAsync_ThrowsForAnUnknownText()
    {
        var generator = new OrderingEmbeddingGenerator(Corpus);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(
            () => generator.GenerateAsync(["not in the corpus"], cancellationToken: ct));
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
```

- [ ] **Step 2: Run it and confirm it fails to compile**

```
dotnet build tests/Rag.NET.Benchmarks.Quality.IntegrationTests
```

Expected: FAIL — `OrderingEmbeddingGenerator` does not exist. A compile failure is the correct "red" here; there is nothing to run yet.

- [ ] **Step 3: Implement the generator**

Create `OrderingEmbeddingGenerator.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A deterministic fixture embedder whose vectors impose a <b>unique</b> ranking, for the parity
/// test's fast leg.
/// <para>
/// Text <i>i</i> of <i>n</i> maps to the 2-D unit vector at angle <c>i·δ</c>, where
/// <c>δ = π / (2(n+1))</c>. Every angle lies in <c>(0, π/2)</c>, so cosine against
/// <see cref="QueryText"/> at angle 0 is strictly decreasing in <i>i</i>: the expected ranking is
/// corpus order, and no two documents can tie.
/// </para>
/// <para>
/// The construction is geometric rather than hashed on purpose. A hash-derived angle is only
/// <i>probably</i> tie-free, and a fixture that is probably non-degenerate is what
/// <see cref="OrderingEmbeddingGeneratorTests"/> exists to refuse.
/// </para>
/// </summary>
/// <remarks>
/// An unknown text throws rather than returning a default vector. A silent fallback is precisely
/// the degenerate-fixture failure mode: every unrecognised text would embed identically and the
/// parity assertion would compare two copies of the same ranking.
/// </remarks>
internal sealed class OrderingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The query this fixture is built around, at angle 0 — nearest to corpus position 0.</summary>
    public const string QueryText = "the parity query";

    private readonly Dictionary<string, float[]> _vectorsByText;

    /// <summary>Creates the generator over a fixed, ordered corpus.</summary>
    /// <param name="orderedTexts">The corpus, in the order retrieval is expected to return it.</param>
    public OrderingEmbeddingGenerator(IReadOnlyList<string> orderedTexts)
    {
        ArgumentNullException.ThrowIfNull(orderedTexts);
        ArgumentOutOfRangeException.ThrowIfZero(orderedTexts.Count);

        OrderedTexts = orderedTexts;

        var delta = Math.PI / (2 * (orderedTexts.Count + 1));
        _vectorsByText = new Dictionary<string, float[]>(
            orderedTexts.Count + 1, StringComparer.Ordinal)
        {
            [QueryText] = [1f, 0f],
        };

        for (var i = 0; i < orderedTexts.Count; i++)
        {
            var angle = i * delta;
            _vectorsByText[orderedTexts[i]] =
                [(float)Math.Cos(angle), (float)Math.Sin(angle)];
        }
    }

    /// <summary>Gets the corpus, in the order retrieval is expected to return it.</summary>
    public IReadOnlyList<string> OrderedTexts { get; }

    /// <inheritdoc/>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            if (!_vectorsByText.TryGetValue(value, out var vector))
            {
                throw new ArgumentException(
                    $"'{value}' is not in this fixture's corpus. Returning a default vector for an " +
                    "unknown text would make every unrecognised text embed identically, which is " +
                    "the degenerate fixture the parity test cannot detect.",
                    nameof(values));
            }

            embeddings.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(embeddings);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) is true ? this : null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
```

- [ ] **Step 4: Run the guard tests and confirm they pass**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: the four `OrderingEmbeddingGeneratorTests` cases PASS. If `Similarities_AreStrictlyDecreasing_AndPairwiseDistinct` fails, **stop** — the fixture is degenerate and nothing built on it means anything.

- [ ] **Step 5: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/OrderingEmbeddingGenerator.cs \
        tests/Rag.NET.Benchmarks.Quality.IntegrationTests/OrderingEmbeddingGeneratorTests.cs
git commit -m "test(parity): add a strictly-ordering fixture embedder with its own guard"
```

---

### Task 2: The comparison helper

One helper, shared by both legs: build a default `AddRagNet` container over a supplied store and embedder, run the query, project to `ChunkHit`, and compare against the harness's ranking with a diagnostic message that names the first differing rank.

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParity.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `internal static async Task<IReadOnlyList<ChunkHit>> RetrieveThroughPipelineAsync(IVectorStore store, IEmbeddingGenerator<string, Embedding<float>> embedder, string query, int topK, CancellationToken ct)`
  - `internal static void AssertSame(IReadOnlyList<ChunkHit> harness, IReadOnlyList<ChunkHit> pipeline, string query)`

- [ ] **Step 1: Write the helper**

Create `PipelineParity.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Holds a real <c>AddRagNet</c> pipeline to the harness's dense row over the same store.
/// <para>
/// <b>"Parity" here does not mean what it means elsewhere in this project.</b> A
/// <see cref="BeirParityTests"/> "parity leg" reproduces a published BEIR figure. This type
/// compares the shipped retrieval pipeline against the harness that produces those figures — a
/// different claim about a different pair of things.
/// </para>
/// <para>
/// Every pinned figure in this project comes from <see cref="BeirHarness"/>, which calls
/// <c>store.SearchAsync</c> directly. A user goes through <c>AddRagNet</c>, whose default retrieval
/// chain is seventeen behaviours deep — sixteen of them before the one the harness calls. All
/// sixteen are supposed to no-op at shipped defaults, and until this type existed nothing asserted
/// it: a behaviour that quietly stopped no-opping would change what every user gets while the
/// figures went on describing the old path, with the suite green.
/// </para>
/// </summary>
internal static class PipelineParity
{
    /// <summary>
    /// Runs one query through a default <c>AddRagNet</c> pipeline over the supplied store.
    /// </summary>
    /// <param name="store">
    /// The populated store, handed to the container as an instance. Sharing it by identity is what
    /// leaves the sixteen behaviours as the only variable — rebuilding an equivalent store would
    /// reintroduce indexing as a second one and make a failure unattributable.
    /// </param>
    /// <param name="embedder">The same embedder the store was indexed through.</param>
    /// <param name="query">The query text.</param>
    /// <param name="topK">Set explicitly: <see cref="RetrievalOptions.TopK"/> defaults to 5 while
    /// the harness computes its own cutoff, so leaving each side on its default would fail for a
    /// reason that is not drift.</param>
    /// <param name="ct">Cancels the retrieval.</param>
    /// <returns>The pipeline's hits, projected to the harness's <see cref="ChunkHit"/> shape.</returns>
    /// <remarks>
    /// A fresh container per call, deliberately. <c>ResultCacheBehavior</c> and
    /// <c>EmbeddingCacheBehavior</c> are both in the default chain; if either is not a no-op, a
    /// re-run could agree where a first run did not. This keeps the test on the first-call path,
    /// which is what a user gets.
    /// </remarks>
    public static async Task<IReadOnlyList<ChunkHit>> RetrieveThroughPipelineAsync(
        IVectorStore store,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string query,
        int topK,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(embedder);
        services.AddRagNet();

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IRagPipeline>();

        var result = await pipeline.RetrieveAsync(
            query, new RetrievalOptions { TopK = topK }, ct);

        if (!result.IsSuccess)
        {
            Assert.Fail(
                $"The pipeline failed to retrieve for '{query}': {result.Error}. This is a failure " +
                "to run, not a parity mismatch — the two are different findings and must not be " +
                "reported as one.");
        }

        return ToChunkHits(result.Value);
    }

    /// <summary>
    /// Asserts the two rankings are identical: same ids, same scores, same order.
    /// </summary>
    /// <param name="harness">The harness's ranking, from <c>AblationRow.Dense</c>.</param>
    /// <param name="pipeline">The pipeline's ranking.</param>
    /// <param name="query">The query, for the message.</param>
    /// <remarks>
    /// Scores are compared exactly. Both sides call the same <c>SearchAsync</c> on the same store,
    /// so identical inputs give bit-identical floats; there is no legitimate source of a small
    /// difference, so a tolerance could only hide an illegitimate one — in particular a query
    /// vector that differs because the pipeline's embedder and the harness's disagree.
    /// </remarks>
    public static void AssertSame(
        IReadOnlyList<ChunkHit> harness,
        IReadOnlyList<ChunkHit> pipeline,
        string query)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(pipeline);

        var shared = Math.Min(harness.Count, pipeline.Count);
        for (var rank = 0; rank < shared; rank++)
        {
            if (harness[rank].ChunkId == pipeline[rank].ChunkId &&
                harness[rank].Score.Equals(pipeline[rank].Score))
            {
                continue;
            }

            Assert.Fail(Explain(rank, harness[rank], pipeline[rank], query));
        }

        Assert.True(
            harness.Count == pipeline.Count,
            $"'{query}' returned {pipeline.Count} hits through the pipeline and {harness.Count} " +
            $"through the harness, agreeing on the first {shared}. {WhatItMeans}");
    }

    private const string WhatItMeans =
        "Either a default retrieval behaviour stopped being a no-op, or the harness's dense path " +
        "changed. If the behaviour change was deliberate, every pinned figure now describes " +
        "something the shipped pipeline no longer does, and the figures — not this test — are what " +
        "need attention.";

    private static string Explain(int rank, ChunkHit harness, ChunkHit pipeline, string query)
    {
        // Equal scores with different ids is a tie-break divergence, not a vector divergence, and
        // it points somewhere else entirely — so it is worth saying rather than leaving the reader
        // to compare two long numbers by eye.
        var sameScore = harness.Score.Equals(pipeline.Score)
            ? " The scores are equal, so this is a tie-break difference rather than a different " +
              "query vector."
            : string.Empty;

        return
            $"Rank {rank} differs for '{query}' — pipeline {pipeline.ChunkId} ({pipeline.Score}) " +
            $"vs harness {harness.ChunkId} ({harness.Score}).{sameScore} {WhatItMeans}";
    }

    /// <summary>
    /// Projects to the harness's hit shape. The id format is copied from
    /// <c>AblationRow.ToChunkHits</c>, which is <c>private protected</c> and cannot be called; this
    /// is the one duplication the design accepts, because the alternative is widening the harness.
    /// </summary>
    private static IReadOnlyList<ChunkHit> ToChunkHits(IReadOnlyList<SearchResult> results)
    {
        var hits = new ChunkHit[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                results[i].Score);
        }

        return hits;
    }
}
```

- [ ] **Step 2: Build**

```
dotnet build Rag.NET.slnx
```

Expected: PASS with 0 warnings. If `result.IsSuccess` / `result.Error` / `result.Value` do not compile, read `ZeroAlloc.Results.Result<T,E>`'s actual surface and adjust — the shape is `Result<IReadOnlyList<SearchResult>, RagError>` and only the member names are in question.

- [ ] **Step 3: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParity.cs
git commit -m "test(parity): add the pipeline-vs-harness comparison helper"
```

---

### Task 3: The fast leg

Runs on every push, no provisioning. Six documents, `TopK = 4` — strictly less than the corpus, so a truncation bug is observable.

**Files:**
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParityTests.cs`

**Interfaces:**
- Consumes: `OrderingEmbeddingGenerator` (Task 1); `PipelineParity.RetrieveThroughPipelineAsync` and `PipelineParity.AssertSame` (Task 2).
- Produces: nothing later tasks consume, except that Task 4 mutates this file's container setup temporarily and Task 5 adds a second case to it.

- [ ] **Step 1: Write the test**

Create `PipelineParityTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Benchmarks.Quality;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.VectorStores.InMemory;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Holds a real <c>AddRagNet</c> pipeline to the harness's dense row. See
/// <see cref="PipelineParity"/> for why this is not the same sense of "parity" the BEIR legs use.
/// </summary>
public sealed class PipelineParityTests
{
    private static readonly string[] Corpus =
    [
        "the first document, nearest the query",
        "the second document",
        "the third document",
        "the fourth document",
        "the fifth document",
        "the sixth document, furthest from the query",
    ];

    /// <summary>Strictly below <see cref="Corpus"/>'s length, so truncation is observable.</summary>
    private const int TopK = 4;

    [Fact]
    public async Task DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnASyntheticCorpus()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new OrderingEmbeddingGenerator(Corpus);

        using var store = new InMemoryVectorStore();
        await IndexAsync(store, embedder, ct);

        // The harness side, expressed as DenseRow expresses it: one query embedding, one cosine
        // search. AblationRow.Dense itself takes a concrete OnnxEmbeddingGenerator, so it cannot be
        // called with a fixture embedder — the real leg calls it directly.
        var queryVectors = await embedder.GenerateAsync(
            [OrderingEmbeddingGenerator.QueryText], cancellationToken: ct);
        var harnessResults = await store.SearchAsync(
            queryVectors[0].Vector, new SearchOptions { TopK = TopK }, ct);
        var harness = Project(harnessResults);

        var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
            store, embedder, OrderingEmbeddingGenerator.QueryText, TopK, ct);

        PipelineParity.AssertSame(harness, pipeline, OrderingEmbeddingGenerator.QueryText);

        // The fixture's ordering is known by construction, so this pins what BOTH sides should have
        // returned. Without it, two identically-wrong rankings would agree and pass.
        Assert.Equal(
            ["doc-0#0", "doc-1#0", "doc-2#0", "doc-3#0"],
            harness.Select(h => h.ChunkId));
    }

    private static async Task IndexAsync(
        IVectorStore store,
        OrderingEmbeddingGenerator embedder,
        CancellationToken ct)
    {
        var vectors = await embedder.GenerateAsync(Corpus, cancellationToken: ct);
        var chunks = new List<EmbeddedChunk>(Corpus.Length);
        for (var i = 0; i < Corpus.Length; i++)
        {
            chunks.Add(new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    Text = Corpus[i],
                    DocumentId = new DocumentId(FormattableString.Invariant($"doc-{i}")),
                    ChunkIndex = 0,
                },
                Embedding = vectors[i].Vector,
            });
        }

        await store.StoreAsync(chunks, ct);
    }

    private static IReadOnlyList<ChunkHit> Project(IReadOnlyList<SearchResult> results)
    {
        var hits = new ChunkHit[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var chunk = results[i].Chunk;
            hits[i] = new ChunkHit(
                FormattableString.Invariant($"{chunk.DocumentId.Value}#{chunk.ChunkIndex}"),
                chunk.DocumentId.Value,
                results[i].Score);
        }

        return hits;
    }
}
```

- [ ] **Step 2: Run it**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: PASS. **A pass here proves nothing yet** — that is Task 4's job. If it fails, read the message: a rank-0 difference with different scores means the pipeline's embedder is not being used; a count difference means a behaviour is truncating or over-fetching.

If the namespace `Rag.NET.VectorStores.InMemory` is wrong, find the real one — `grep -rn "class InMemoryVectorStore" src --include=*.cs` — and fix the using. Do not work around it by constructing a different store.

- [ ] **Step 3: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParityTests.cs
git commit -m "test(parity): assert the default pipeline matches the harness on a synthetic corpus"
```

---

### Task 4: Make it fail on purpose

**This task is not optional and its deliverable is evidence, not code.** The test passed the moment it was written, which is the expected state — and a test that has never failed is not evidence of anything. This project's last three fixture defects survived exactly this way.

**Files:**
- Temporarily modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParity.cs`
- Nothing is committed from this task except the notes in Task 6's roadmap entry.

- [ ] **Step 1: Verify the mutation compiles before running anything**

In `PipelineParity.RetrieveThroughPipelineAsync`, change the registration line to enable one default behaviour:

```csharp
services.AddRagNet(configure: rag => rag.UseMmr());
```

```
dotnet build Rag.NET.slnx
```

Expected: PASS. If `UseMmr()` is not the method name, find the real one (`grep -rn "MmrOptions\|UseMmr" src --include=*.cs`) and use it. **Verify the mutation compiles before running it** — a mutation that does not build proves nothing about the test, and this is the convention 6.2.12 established.

- [ ] **Step 2: Run the fast leg and confirm it goes RED**

```
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: **FAIL**, on `DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnASyntheticCorpus`.

- [ ] **Step 3: Read the message and check it is actually useful**

The failure must name a specific rank with both ids and both scores. Confirm all three:

1. it names the **first** differing rank, not merely "collections differ";
2. it prints both chunk ids and both scores;
3. it says what the difference means — a behaviour stopped no-opping, or the harness changed.

If the message is not diagnostic, fix `PipelineParity.Explain` **now**, while there is a real failure to read it against. A diagnostic written against an imagined failure is how unhelpful messages ship.

- [ ] **Step 4: Revert the mutation**

```bash
git checkout tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParity.cs
```

- [ ] **Step 5: Confirm green again**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected: PASS. Record what the failure message said — Task 6 puts it in the roadmap entry, because "this test has been observed failing, and here is what it said" is the evidence that makes the green meaningful.

---

### Task 5: The real leg

SciFact, the real ONNX embedder, one store shared by identity, and the harness's **own** `AblationRow.Dense` on the other side. Skips rather than fails when unprovisioned.

**Files:**
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParityTests.cs`

**Interfaces:**
- Consumes: `PipelineParity` (Task 2); `BeirHarness.IsProvisioned`, `BeirHarness.LoadAsync`, `BeirHarness.CreateGenerator`, `BeirHarness.OneChunkPerDocument`, `BeirHarness.ModelIdentity`, `BeirHarness.EmbedAsync` (internal, same assembly); `AblationRow.Dense`; `CachingEmbeddingGenerator` (internal, existing).
- Produces: nothing.

- [ ] **Step 1: Add the test method to `PipelineParityTests`**

Add these usings at the top of the file:

```csharp
using Rag.NET.Embeddings.Onnx;
```

And this method to the class:

```csharp
    /// <summary>How many queries the real leg compares — fixed, so the run is seconds.</summary>
    private const int RealLegQueryCount = 20;

    /// <summary>
    /// The same claim on the corpus the pinned figures come from, against the harness's own dense
    /// row rather than a restatement of it.
    /// </summary>
    /// <remarks>
    /// Gated on provisioning only, deliberately — not on <c>RAGNET_BEIR_LONG_RUNS</c>. The
    /// embeddings are cached and twenty queries are seconds; the long-run gate exists for
    /// hour-scale sweeps, and putting the honest leg behind it would mean it effectively never
    /// runs.
    /// </remarks>
    [Fact]
    public async Task DefaultPipeline_ReturnsWhatTheHarnessDenseRowReturns_OnSciFact()
    {
        Assert.SkipUnless(
            BeirHarness.IsProvisioned(out var modelPath, out var vocabPath, out var cacheDirectory),
            BeirHarness.SkipReason);

        var ct = TestContext.Current.CancellationToken;
        var descriptor = BeirDatasetDescriptor.SciFact;

        // The separator is passed explicitly for the same reason BeirParityTests passes it: it
        // decides what is embedded, and the cached vectors were produced with a single space.
        var dataset = await BeirHarness.LoadAsync(descriptor, cacheDirectory, " ", ct);

        using var generator = BeirHarness.CreateGenerator(modelPath, vocabPath);
        var embeddings = new EmbeddingCache(cacheDirectory, BeirHarness.ModelIdentity);

        var units = BeirHarness.OneChunkPerDocument(dataset.Documents);

        // One store, indexed once, handed to both sides. This is what makes the sixteen behaviours
        // the only surviving variable.
        using var store = new InMemoryVectorStore();
        var unitTexts = units.Select(u => u.Text).ToArray();
        var unitVectors = await BeirHarness.EmbedAsync(generator, embeddings, unitTexts, ct);

        var chunks = new List<EmbeddedChunk>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            chunks.Add(new EmbeddedChunk { Chunk = units[i], Embedding = unitVectors[i] });
        }

        await store.StoreAsync(chunks, ct);

        // The pipeline reads the identical cached vector rather than calling the generator live: a
        // cache populated under a different model revision would otherwise disagree with a live
        // generator, and that difference is not the one this test is about.
        var pipelineEmbedder = new CachingEmbeddingGenerator(generator, embeddings);

        var queries = dataset.Queries
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .Take(RealLegQueryCount)
            .ToArray();

        Assert.Equal(RealLegQueryCount, queries.Length);

        var searchOptions = new SearchOptions { TopK = TopK };
        foreach (var query in queries)
        {
            var harness = await AblationRow.Dense.RetrieveAsync(
                query, generator, embeddings, store, searchOptions, ct);

            var pipeline = await PipelineParity.RetrieveThroughPipelineAsync(
                store, pipelineEmbedder, query.Text, TopK, ct);

            PipelineParity.AssertSame(harness, pipeline, query.Text);
        }
    }
```

- [ ] **Step 2: Run it**

```
dotnet build Rag.NET.slnx
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests --no-build
```

Expected on a provisioned machine: PASS. Expected unprovisioned: **SKIP** with `BeirHarness.SkipReason` — a skip is the correct outcome, never a failure.

**If it skips, say so plainly when reporting.** A skipped leg is not a passing leg, and this project has already recorded a case where two guards reporting `[SKIP]` were nearly read as green.

- [ ] **Step 3: Commit**

```bash
git add tests/Rag.NET.Benchmarks.Quality.IntegrationTests/PipelineParityTests.cs
git commit -m "test(parity): add the SciFact leg against the harness's own dense row"
```

---

### Task 6: Record the thread — not the phase

**Files:**
- Modify: `docs/planning/ROADMAP.md` (Phase 6.2.1's block)
- Modify: `docs/planning/STATE.md`

- [ ] **Step 1: Update the 6.2.1 open-threads list**

In `docs/planning/ROADMAP.md`, find the "Open threads, 2026-08-20" paragraph in Phase 6.2.1's block and strike the pipeline-parity item the way the RAPTOR item was struck, adding a paragraph recording: both legs, the shared-store construction, and — most importantly — **that the mutation check was run and what the failure message said**.

**Do not mark Phase 6.2.1 complete.** It still owes HyDE, reranking, hybrid BM25, late chunking, SPLADE, the three answer engines as arms, every vector store through the SciFact parity leg, the second-corpus RAPTOR arm, and local search's unexplained yes/no abstention.

- [ ] **Step 2: Note the exit-condition clause**

6.2.1's exit condition includes *"the pipeline-parity test is in the fast tier"*. Record that this clause is now met, and state whether the real leg ran or skipped on the machine that built it.

- [ ] **Step 3: Update `STATE.md`**

Set the Working State branch to `feat/pipeline-parity-test`, update Last completed, and set the next recommended step. **Also fix the Working State branch field before the branch merges** — it has gone stale six times, always at the moment its branch merged.

- [ ] **Step 4: Verify and commit**

```
dotnet test tests/Rag.NET.RepoConventions.Tests --no-build
git add docs/planning/ROADMAP.md docs/planning/STATE.md
git commit -m "chore(roadmap): record the pipeline-parity thread in phase 6.2.1"
git status
```

Expected: conventions tests pass, clean tree.

---

## Self-review

**Spec coverage.** Fast leg → Task 3. Fixture contract with its own guard → Task 1. Real leg, shared store, `AblationRow.Dense` → Task 5. Comparison level, exact scores, explicit `TopK` → Task 2 + Task 3. Failure semantics, `RagError` handled separately, fresh container per call, several queries → Task 2 + Task 5. Naming-collision disambiguation → Task 2's doc comment. Mutation check → Task 4. Roadmap without phase completion → Task 6. No spec section is unimplemented.

**Placeholder scan.** No TBD/TODO. Every code step carries real code. The two places that say "if X does not compile, find the real name" are bounded verification steps with the exact `grep` to run, not deferred design.

**Type consistency.** `ChunkHit(string ChunkId, string DocumentId, double Score)` is used identically in Tasks 2, 3 and 5. `OrderingEmbeddingGenerator.QueryText` is defined in Task 1 and consumed in Task 3. `PipelineParity.RetrieveThroughPipelineAsync` and `AssertSame` keep the same signatures across Tasks 3 and 5. `TopK` is a private const on `PipelineParityTests`, defined in Task 3 and reused in Task 5.

**One known risk left in the plan deliberately.** Task 3 duplicates the `"{DocumentId}#{ChunkIndex}"` id format because `AblationRow.ToChunkHits` is `private protected`. Both copies are in files this plan creates, and Global Constraints names the duplication so a reviewer sees it was chosen rather than missed.
