# Embedding Distance Evaluation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a new `Rag.NET.Evaluation` project that lets callers score RAG answer quality by comparing cosine similarity between embedded predicted and reference answers.

**Architecture:** New project `src/Rag.NET.Evaluation/` with three types: `EvaluationSample` (record), `EvaluationResult` (record), `IRagEvaluator` (interface), and `EmbeddingDistanceEvaluator` (implementation). The evaluator embeds all predicted and reference texts in two batch calls, computes pairwise cosine similarity, and returns a mean score. The project references only `Rag.NET` core (for `IEmbeddingGenerator`) and `Microsoft.Extensions.AI.Abstractions`.

**Tech Stack:** C# 13, .NET 10, `Microsoft.Extensions.AI.Abstractions`, xunit.v3, NSubstitute.

---

### Task 1: Create the `Rag.NET.Evaluation` project

**Files:**
- Create: `src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj`
- Create: `src/Rag.NET.Evaluation/EvaluationSample.cs`
- Create: `src/Rag.NET.Evaluation/EvaluationResult.cs`
- Create: `src/Rag.NET.Evaluation/IRagEvaluator.cs`

**Step 1: Create the project file**

Create `src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Evaluation</RootNamespace>
    <PackageId>Rag.NET.Evaluation</PackageId>
    <Description>Evaluation utilities for Rag.NET pipelines</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
  </ItemGroup>

</Project>
```

**Step 2: Create `EvaluationSample`**

Create `src/Rag.NET.Evaluation/EvaluationSample.cs`:

```csharp
namespace Rag.NET.Evaluation;

/// <summary>A single question/answer pair to evaluate.</summary>
public sealed record EvaluationSample(
    string Question,
    string PredictedAnswer,
    string ReferenceAnswer);
```

**Step 3: Create `EvaluationResult`**

Create `src/Rag.NET.Evaluation/EvaluationResult.cs`:

```csharp
namespace Rag.NET.Evaluation;

/// <summary>
/// Result of an evaluation run.
/// <see cref="MeanScore"/> is the average cosine similarity across all samples (0–1, higher is better).
/// </summary>
public sealed record EvaluationResult(
    double MeanScore,
    IReadOnlyList<double> Scores);
```

**Step 4: Create `IRagEvaluator`**

Create `src/Rag.NET.Evaluation/IRagEvaluator.cs`:

```csharp
namespace Rag.NET.Evaluation;

public interface IRagEvaluator
{
    Task<EvaluationResult> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default);
}
```

**Step 5: Build to confirm project compiles**

```bash
dotnet build src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj -v minimal
```

Expected: Build succeeded, 0 errors.

**Step 6: Commit**

```bash
git add src/Rag.NET.Evaluation/
git commit -m "feat: add Rag.NET.Evaluation project with IRagEvaluator and model types"
```

---

### Task 2: Implement `EmbeddingDistanceEvaluator`

**Files:**
- Create: `src/Rag.NET.Evaluation/EmbeddingDistanceEvaluator.cs`
- Create: `tests/Rag.NET.Evaluation.Tests/Rag.NET.Evaluation.Tests.csproj`
- Create: `tests/Rag.NET.Evaluation.Tests/EmbeddingDistanceEvaluatorTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Evaluation.Tests/Rag.NET.Evaluation.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Rag.NET.Evaluation\Rag.NET.Evaluation.csproj" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
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

Create `tests/Rag.NET.Evaluation.Tests/EmbeddingDistanceEvaluatorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class EmbeddingDistanceEvaluatorTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(
        params ReadOnlyMemory<float>[] vectorsInOrder)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var callCount = 0;
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var result = texts.Select((_, i) =>
                    new Embedding<float>(vectorsInOrder[callCount + i])).ToList();
                callCount += texts.Count;
                return Task.FromResult<IList<Embedding<float>>>(result);
            });
        return embedder;
    }

    [Fact]
    public async Task EvaluateAsync_EmptySamples_ThrowsArgumentException()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var evaluator = new EmbeddingDistanceEvaluator(embedder);
        await Assert.ThrowsAsync<ArgumentException>(
            () => evaluator.EvaluateAsync([]));
    }

    [Fact]
    public async Task EvaluateAsync_IdenticalEmbeddings_ScoreIsOne()
    {
        // Predicted and reference have identical embeddings → cosine = 1.0
        var vec = new float[] { 1f, 0f, 0f };
        var embedder = MakeEmbedder(vec, vec); // one predicted, one reference
        var evaluator = new EmbeddingDistanceEvaluator(embedder);

        var result = await evaluator.EvaluateAsync([
            new EvaluationSample("Q", "Answer A", "Answer A")
        ]);

        Assert.Equal(1, result.Scores.Count);
        Assert.True(result.Scores[0] > 0.99, $"Expected ~1.0 but got {result.Scores[0]}");
        Assert.True(result.MeanScore > 0.99);
    }

    [Fact]
    public async Task EvaluateAsync_OrthogonalEmbeddings_ScoreIsZero()
    {
        // Predicted and reference are orthogonal → cosine = 0.0
        var embedder = MakeEmbedder(
            new float[] { 1f, 0f, 0f }, // predicted
            new float[] { 0f, 1f, 0f }  // reference
        );
        var evaluator = new EmbeddingDistanceEvaluator(embedder);

        var result = await evaluator.EvaluateAsync([
            new EvaluationSample("Q", "Unrelated", "Something else")
        ]);

        Assert.True(result.Scores[0] < 0.01, $"Expected ~0.0 but got {result.Scores[0]}");
    }

    [Fact]
    public async Task EvaluateAsync_MultipleSamples_MeanScoreIsAverage()
    {
        // Two samples: one perfect (1.0), one orthogonal (0.0) → mean = 0.5
        var embedder = MakeEmbedder(
            new float[] { 1f, 0f, 0f }, // predicted 1
            new float[] { 0f, 1f, 0f }, // predicted 2
            new float[] { 1f, 0f, 0f }, // reference 1 (same as predicted 1)
            new float[] { 1f, 0f, 0f }  // reference 2 (orthogonal to predicted 2)
        );
        var evaluator = new EmbeddingDistanceEvaluator(embedder);

        var result = await evaluator.EvaluateAsync([
            new EvaluationSample("Q1", "Answer 1", "Answer 1"),
            new EvaluationSample("Q2", "Answer 2", "Different")
        ]);

        Assert.Equal(2, result.Scores.Count);
        Assert.True(result.Scores[0] > 0.99);
        Assert.True(result.Scores[1] < 0.01);
        Assert.True(Math.Abs(result.MeanScore - 0.5) < 0.01, $"Expected ~0.5 but got {result.MeanScore}");
    }

    [Fact]
    public async Task EvaluateAsync_NullSamples_ThrowsArgumentNullException()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var evaluator = new EmbeddingDistanceEvaluator(embedder);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => evaluator.EvaluateAsync(null!));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests -v minimal
```

Expected: FAIL — `EmbeddingDistanceEvaluator` does not exist.

**Step 3: Implement `EmbeddingDistanceEvaluator`**

Create `src/Rag.NET.Evaluation/EmbeddingDistanceEvaluator.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation;

/// <summary>
/// Evaluates RAG answer quality by comparing cosine similarity between
/// embeddings of predicted and reference answers.
/// Score of 1.0 = identical semantic content; 0.0 = completely unrelated.
/// </summary>
public sealed class EmbeddingDistanceEvaluator(
    IEmbeddingGenerator<string, Embedding<float>> embedder) : IRagEvaluator
{
    public async Task<EvaluationResult> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));

        var predictedTexts = samples.Select(s => s.PredictedAnswer).ToList();
        var referenceTexts = samples.Select(s => s.ReferenceAnswer).ToList();

        var predictedEmbeddings = await embedder.GenerateAsync(predictedTexts, cancellationToken: cancellationToken).ConfigureAwait(false);
        var referenceEmbeddings = await embedder.GenerateAsync(referenceTexts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var scores = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            scores[i] = CosineSimilarity(predictedEmbeddings[i].Vector, referenceEmbeddings[i].Vector);
        }

        var meanScore = scores.Average();
        return new EvaluationResult(meanScore, scores);
    }

    private static double CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;

        if (spanA.Length != spanB.Length)
            return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < spanA.Length; i++)
        {
            dot += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0.0 ? 0.0 : dot / denom;
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests -v minimal
```

Expected: all PASS.

**Step 5: Commit**

```bash
git add src/Rag.NET.Evaluation/EmbeddingDistanceEvaluator.cs tests/Rag.NET.Evaluation.Tests/
git commit -m "feat: implement EmbeddingDistanceEvaluator with cosine similarity scoring"
```

---

### Task 3: Add the new projects to the solution and verify full build

**Step 1: Add projects to solution**

```bash
dotnet sln add src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj
dotnet sln add tests/Rag.NET.Evaluation.Tests/Rag.NET.Evaluation.Tests.csproj
```

**Step 2: Build entire solution**

```bash
dotnet build -v minimal
```

Expected: Build succeeded, 0 errors.

**Step 3: Run all tests**

```bash
dotnet test -v minimal
```

Expected: all tests pass including new evaluation tests.

**Step 4: Commit**

```bash
git add *.sln
git commit -m "chore: add Rag.NET.Evaluation and its tests to solution"
```

---

### Task 4: Update `docs/features.md` to mark Embedding Distance Evaluation as done

**Files:**
- Modify: `docs/features.md`

**Step 1: Find the Embedding Distance Evaluation row**

Open `docs/features.md` and locate the row for "Embedding Distance Evaluation" in the priority table.
Change `[ ]` to `[x]`.

**Step 2: Verify the file looks right**

```bash
grep -n "Embedding Distance" docs/features.md
```

Expected: line with `[x]`.

**Step 3: Commit**

```bash
git add docs/features.md
git commit -m "docs: mark Embedding Distance Evaluation as complete"
```
