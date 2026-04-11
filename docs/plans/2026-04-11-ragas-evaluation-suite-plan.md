# RAGAS Evaluation Suite Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a complete RAGAS-style evaluation suite (Faithfulness, Answer Relevance, Context Precision, Context Recall) and a synthetic dataset builder to Rag.NET, in a new `Rag.NET.Evaluation.Ragas` package.

**Architecture:** `EvaluationDatasetBuilder` (in existing `Rag.NET.Evaluation`) samples chunks from `IRagDataManager`, generates Q&A pairs via LLM. A new `Rag.NET.Evaluation.Ragas` package contains four `IRagasMetric` implementations and a fluent `RagasEvaluationSuiteBuilder` that runs registered metrics concurrently per sample. Context Precision/Recall throw `InvalidOperationException` on empty `ReferenceAnswer` — fail fast.

**Tech Stack:** `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`), `NSubstitute`, `xunit.v3`, `System.Numerics.Tensors` (cosine similarity).

---

## Context for the implementer

### Key existing types (do not change signatures)

```csharp
// Rag.NET.Evaluation
public sealed record EvaluationSample(
    string Question,
    string PredictedAnswer,
    string ReferenceAnswer,
    IReadOnlyList<string>? SourceChunks = null);

public interface IRagEvaluator
{
    Task<EvaluationResult> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default);
}

// Rag.NET.Abstractions
public interface IRagDataManager
{
    Task<IReadOnlyList<DocumentSummary>> GetDocumentsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TextChunk>> GetChunksAsync(string documentId, CancellationToken cancellationToken = default);
    // ... other members not needed here
}

public sealed record TextChunk
{
    public required string Text { get; init; }
    public required DocumentId DocumentId { get; init; }
    public required int ChunkIndex { get; init; }
    // ...
}
```

### Test conventions

- `TestContext.Current.CancellationToken` for all async tests
- `NSubstitute` for mocks (`Substitute.For<T>()`)
- `xunit.v3` (`[Fact]`, `[Theory]`)
- Tests in `tests/Rag.NET.Tests/Evaluation/`

### `LlmJudgeEvaluator` pattern (follow this for all LLM calls)

The existing `LlmJudgeEvaluator` uses `IChatClient.GetResponseAsync(messages)` with a system prompt + user prompt built via `StringBuilder`. Responses are JSON-parsed. Follow the same prompt-building and JSON-parsing pattern for all new evaluators.

### Cosine similarity

Use `TensorPrimitives.CosineSimilarity(ReadOnlySpan<float>, ReadOnlySpan<float>)` from `System.Numerics.Tensors` (already available in .NET 10). `IEmbeddingGenerator` returns `GeneratedEmbeddings<Embedding<float>>`; access the vector via `.Vector.Span`.

---

## Task 1: `EvaluationDatasetBuilder` — tests first

**Files:**
- Create: `tests/Rag.NET.Tests/Evaluation/EvaluationDatasetBuilderTests.cs`

**Step 1: Write failing tests**

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class EvaluationDatasetBuilderTests
{
    private static IRagDataManager MakeDataManager(params string[] chunkTexts)
    {
        var manager = Substitute.For<IRagDataManager>();
        var docId = new DocumentId("doc-1");
        var summary = new DocumentSummary
        {
            DocumentId = docId, FileName = "test.txt",
            ChunkCount = chunkTexts.Length,
            IngestedAt = DateTimeOffset.UtcNow,
        };
        manager.GetDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DocumentSummary> { summary });
        var chunks = chunkTexts.Select((t, i) => new TextChunk
        {
            Text = t, DocumentId = docId, ChunkIndex = i,
        }).ToList();
        manager.GetChunksAsync(docId.Value, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TextChunk>)chunks);
        return manager;
    }

    [Fact]
    public async Task BuildAsync_QuestionOnly_ReturnsSamplesWithEmptyReferenceAnswer()
    {
        var manager = MakeDataManager("Chunk A", "Chunk B", "Chunk C");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "What is chunk A about?")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 2, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Equal(string.Empty, s.ReferenceAnswer));
        Assert.All(samples, s => Assert.NotEmpty(s.Question));
    }

    [Fact]
    public async Task BuildAsync_QuestionAndAnswer_ReturnsSamplesWithReferenceAnswer()
    {
        var manager = MakeDataManager("Chunk A", "Chunk B");
        var client = Substitute.For<IChatClient>();
        // First call = question, second call = answer
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "What is chunk A?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Chunk A is about X.")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 1, Mode = DatasetGenerationMode.QuestionAndAnswer },
            TestContext.Current.CancellationToken);

        Assert.Single(samples);
        Assert.NotEmpty(samples[0].ReferenceAnswer);
    }

    [Fact]
    public async Task BuildAsync_SampleCountExceedsChunks_ClampsToAvailable()
    {
        var manager = MakeDataManager("Only chunk");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q?")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 100 },
            TestContext.Current.CancellationToken);

        Assert.Single(samples);
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "EvaluationDatasetBuilderTests" -v q
```

Expected: compile error — `EvaluationDatasetBuilder`, `EvaluationDatasetBuilderOptions`, `DatasetGenerationMode` not found.

**Step 3: Commit tests**

```bash
git add tests/Rag.NET.Tests/Evaluation/EvaluationDatasetBuilderTests.cs
git commit -m "test(evaluation): add failing tests for EvaluationDatasetBuilder"
```

---

## Task 2: `EvaluationDatasetBuilder` — implementation

**Files:**
- Create: `src/Rag.NET.Evaluation/EvaluationDatasetBuilderOptions.cs`
- Create: `src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs`
- Modify: `src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj` — add `ProjectReference` to `Rag.NET.Abstractions`

**Step 1: Add `Rag.NET.Abstractions` reference**

In `src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj`, add:
```xml
<ItemGroup>
  <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
</ItemGroup>
```

**Step 2: Create options**

`src/Rag.NET.Evaluation/EvaluationDatasetBuilderOptions.cs`:
```csharp
namespace Rag.NET.Evaluation;

public enum DatasetGenerationMode { QuestionOnly, QuestionAndAnswer }

public sealed class EvaluationDatasetBuilderOptions
{
    /// <summary>Number of chunks to sample. Clamped to available chunk count.</summary>
    public int SampleCount { get; init; } = 20;

    /// <summary>
    /// <see cref="DatasetGenerationMode.QuestionOnly"/> produces samples with an empty
    /// <see cref="EvaluationSample.ReferenceAnswer"/> — 1 LLM call per chunk.
    /// <see cref="DatasetGenerationMode.QuestionAndAnswer"/> adds a second LLM call to
    /// generate a ground-truth answer — required for Context Precision/Recall metrics.
    /// </summary>
    public DatasetGenerationMode Mode { get; init; } = DatasetGenerationMode.QuestionOnly;
}
```

**Step 3: Create builder**

`src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs`:
```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation;

/// <summary>
/// Generates synthetic evaluation samples from an existing document corpus.
/// Samples random chunks from <see cref="IRagDataManager"/> and uses an LLM
/// to generate a question (and optionally a reference answer) per chunk.
/// </summary>
public sealed class EvaluationDatasetBuilder(
    IRagDataManager dataManager,
    IChatClient chatClient)
{
    public async Task<IReadOnlyList<EvaluationSample>> BuildAsync(
        EvaluationDatasetBuilderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvaluationDatasetBuilderOptions();

        // Collect all chunks across all documents
        var documents = await dataManager.GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
        var allChunks = new List<TextChunk>();
        foreach (var doc in documents)
        {
            var chunks = await dataManager.GetChunksAsync(doc.DocumentId.Value, cancellationToken).ConfigureAwait(false);
            allChunks.AddRange(chunks);
        }

        // Random sample without replacement, clamped to available count
        var sampleCount = Math.Min(options.SampleCount, allChunks.Count);
        var sampled = allChunks.OrderBy(_ => Random.Shared.Next()).Take(sampleCount).ToList();

        // Generate samples concurrently
        var tasks = sampled.Select(chunk => GenerateSampleAsync(chunk, options.Mode, cancellationToken));
        var samples = await Task.WhenAll(tasks).ConfigureAwait(false);
        return samples;
    }

    private async Task<EvaluationSample> GenerateSampleAsync(
        TextChunk chunk,
        DatasetGenerationMode mode,
        CancellationToken ct)
    {
        var question = await GenerateQuestionAsync(chunk.Text, ct).ConfigureAwait(false);

        var referenceAnswer = string.Empty;
        if (mode == DatasetGenerationMode.QuestionAndAnswer)
            referenceAnswer = await GenerateAnswerAsync(chunk.Text, question, ct).ConfigureAwait(false);

        return new EvaluationSample(
            Question: question,
            PredictedAnswer: string.Empty,
            ReferenceAnswer: referenceAnswer,
            SourceChunks: [chunk.Text]);
    }

    private async Task<string> GenerateQuestionAsync(string chunkText, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Generate a single question whose answer is found in the provided text. " +
                "Output only the question, no explanation."),
            new(ChatRole.User, chunkText),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }

    private async Task<string> GenerateAnswerAsync(string chunkText, string question, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Answer the question using only the provided text. " +
                "Output only the answer, no explanation."),
            new(ChatRole.User, $"Text: {chunkText}\n\nQuestion: {question}"),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }
}
```

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "EvaluationDatasetBuilderTests" -v q
```

Expected: all 3 tests pass.

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
```

Expected: all tests pass, no regressions.

**Step 6: Commit**

```bash
git add src/Rag.NET.Evaluation/EvaluationDatasetBuilderOptions.cs src/Rag.NET.Evaluation/EvaluationDatasetBuilder.cs src/Rag.NET.Evaluation/Rag.NET.Evaluation.csproj
git commit -m "feat(evaluation): add EvaluationDatasetBuilder with QuestionOnly and QuestionAndAnswer modes"
```

---

## Task 3: `Rag.NET.Evaluation.Ragas` — project + shared types

**Files:**
- Create: `src/Rag.NET.Evaluation.Ragas/Rag.NET.Evaluation.Ragas.csproj`
- Create: `src/Rag.NET.Evaluation.Ragas/IRagasMetric.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/RagasReport.cs`

**Step 1: Create the csproj**

`src/Rag.NET.Evaluation.Ragas/Rag.NET.Evaluation.Ragas.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Rag.NET.Evaluation.Ragas</RootNamespace>
    <PackageId>Rag.NET.Evaluation.Ragas</PackageId>
    <Description>RAGAS-style evaluation metrics for Rag.NET pipelines</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Evaluation\Rag.NET.Evaluation.csproj" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: Add the project to the solution**

```bash
dotnet sln add src/Rag.NET.Evaluation.Ragas/Rag.NET.Evaluation.Ragas.csproj
```

**Step 3: Add project reference from test project**

Read `tests/Rag.NET.Tests/Rag.NET.Tests.csproj` and add:
```xml
<ProjectReference Include="..\..\src\Rag.NET.Evaluation.Ragas\Rag.NET.Evaluation.Ragas.csproj" />
```

**Step 4: Create the internal metric interface**

`src/Rag.NET.Evaluation.Ragas/IRagasMetric.cs`:
```csharp
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

internal interface IRagasMetric
{
    /// <summary>True if this metric requires a non-empty ReferenceAnswer on every sample.</summary>
    bool RequiresGroundTruth { get; }

    /// <summary>Score a single sample (0.0–1.0, higher is better).</summary>
    Task<double> ScoreAsync(EvaluationSample sample, CancellationToken ct);
}
```

**Step 5: Create `RagasReport`**

`src/Rag.NET.Evaluation.Ragas/RagasReport.cs`:
```csharp
namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Aggregated RAGAS scores across a set of evaluation samples.
/// Null values indicate a metric was not registered in the suite.
/// <see cref="OverallScore"/> is the mean of all registered (non-null) metrics.
/// </summary>
public sealed record RagasReport
{
    public double? Faithfulness     { get; init; }
    public double? AnswerRelevance  { get; init; }
    public double? ContextPrecision { get; init; }
    public double? ContextRecall    { get; init; }
    public double OverallScore      { get; init; }
}
```

**Step 6: Build to verify**

```
dotnet build src/Rag.NET.Evaluation.Ragas/Rag.NET.Evaluation.Ragas.csproj -v q
```

Expected: build succeeds.

**Step 7: Commit**

```bash
git add src/Rag.NET.Evaluation.Ragas/ tests/Rag.NET.Tests/Rag.NET.Tests.csproj
git commit -m "feat(evaluation): add Rag.NET.Evaluation.Ragas project scaffold"
```

---

## Task 4: `FaithfulnessEvaluator` — tests + implementation

**Files:**
- Create: `tests/Rag.NET.Tests/Evaluation/FaithfulnessEvaluatorTests.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs`

**Spec:** Extract atomic claims from `PredictedAnswer` via LLM, then verify each claim against `SourceChunks` via a second LLM call. Score = verified claims / total claims. Requires `SourceChunks` (non-null, non-empty).

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Evaluation/FaithfulnessEvaluatorTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class FaithfulnessEvaluatorTests
{
    private static EvaluationSample MakeSample(string[] chunks) =>
        new("What is X?", "X is Y and Z.", "X is Y.", chunks);

    [Fact]
    public async Task ScoreAsync_AllClaimsSupported_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        // First call: extract claims → ["X is Y", "X is Z"]
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[\"X is Y\",\"X is Z\"]")),
                // Second call: verify "X is Y" → yes
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                // Third call: verify "X is Z" → yes
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new FaithfulnessEvaluator(client);
        var score = await evaluator.ScoreAsync(MakeSample(["X is Y. X is Z."]), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_NoClaimsSupported_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[\"hallucinated claim\"]")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "no")));

        var evaluator = new FaithfulnessEvaluator(client);
        var score = await evaluator.ScoreAsync(MakeSample(["Unrelated context."]), TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_EmptySourceChunks_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new FaithfulnessEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", SourceChunks: []);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score);
        await client.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FaithfulnessEvaluatorTests" -v q
```

Expected: compile error — `FaithfulnessEvaluator` not found.

**Step 3: Implement**

`src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs`:
```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Faithfulness: fraction of claims in the predicted answer that are
/// supported by the retrieved source chunks.
/// Score = supported_claims / total_claims (0–1, higher = more grounded).
/// </summary>
public sealed class FaithfulnessEvaluator(IChatClient chatClient) : IRagasMetric
{
    public bool RequiresGroundTruth => false;

    public async Task<double> ScoreAsync(EvaluationSample sample, CancellationToken ct)
    {
        if (sample.SourceChunks is not { Count: > 0 })
            return 0.0;

        var claims = await ExtractClaimsAsync(sample.PredictedAnswer, ct).ConfigureAwait(false);
        if (claims.Count == 0)
            return 1.0; // no claims = trivially faithful

        var context = string.Join("\n", sample.SourceChunks);
        var verificationTasks = claims.Select(claim => VerifyClaimAsync(claim, context, ct));
        var results = await Task.WhenAll(verificationTasks).ConfigureAwait(false);

        return results.Count(r => r) / (double)results.Length;
    }

    private async Task<IReadOnlyList<string>> ExtractClaimsAsync(string answer, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Extract all atomic factual claims from the answer. " +
                "Output a JSON array of strings — one string per claim. No explanation."),
            new(ChatRole.User, answer),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var raw = response.Messages.LastOrDefault()?.Text?.Trim() ?? "[]";
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<bool> VerifyClaimAsync(string claim, string context, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Answer only 'yes' or 'no': is the following claim supported by the provided context?"),
            new(ChatRole.User,
                new StringBuilder()
                    .AppendLine(CultureInfo.InvariantCulture, $"Context: {context}")
                    .AppendLine(CultureInfo.InvariantCulture, $"Claim: {claim}")
                    .ToString()),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var answer = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        return answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "FaithfulnessEvaluatorTests" -v q
```

Expected: all 3 tests pass.

**Step 5: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
```

**Step 6: Commit**

```bash
git add src/Rag.NET.Evaluation.Ragas/FaithfulnessEvaluator.cs tests/Rag.NET.Tests/Evaluation/FaithfulnessEvaluatorTests.cs
git commit -m "feat(evaluation): add FaithfulnessEvaluator"
```

---

## Task 5: `AnswerRelevanceEvaluator` — tests + implementation

**Files:**
- Create: `tests/Rag.NET.Tests/Evaluation/AnswerRelevanceEvaluatorTests.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/AnswerRelevanceEvaluator.cs`

**Spec:** Generate `n=3` synthetic questions from the predicted answer via LLM. Embed each synthetic question and the original question via `IEmbeddingGenerator`. Score = mean cosine similarity between synthetic question embeddings and original question embedding.

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Evaluation/AnswerRelevanceEvaluatorTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class AnswerRelevanceEvaluatorTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(float[] vector)
    {
        var gen = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        gen.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                    embeddings.Add(new Embedding<float>(vector));
                return Task.FromResult(embeddings);
            });
        return gen;
    }

    [Fact]
    public async Task ScoreAsync_IdenticalEmbeddings_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        // Returns 3 synthetic questions (one per call)
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q1?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q2?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q3?")));

        // All embeddings identical → cosine = 1.0
        var embedder = MakeEmbedder([1f, 0f, 0f]);
        var evaluator = new AnswerRelevanceEvaluator(client, embedder);
        var sample = new EvaluationSample("What is X?", "X is Y.", string.Empty);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_OrthogonalEmbeddings_ReturnsZero()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q1?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q2?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q3?")));

        var callCount = 0;
        var gen = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        gen.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                {
                    // Alternate between orthogonal vectors
                    embeddings.Add(callCount++ % 2 == 0
                        ? new Embedding<float>([1f, 0f])
                        : new Embedding<float>([0f, 1f]));
                }
                return Task.FromResult(embeddings);
            });

        var evaluator = new AnswerRelevanceEvaluator(client, gen);
        var sample = new EvaluationSample("What is X?", "X is Y.", string.Empty);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score, precision: 2);
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AnswerRelevanceEvaluatorTests" -v q
```

Expected: compile error — `AnswerRelevanceEvaluator` not found.

**Step 3: Implement**

`src/Rag.NET.Evaluation.Ragas/AnswerRelevanceEvaluator.cs`:
```csharp
using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Answer Relevance: mean cosine similarity between embeddings of n=3 synthetic questions
/// (generated from the predicted answer) and the embedding of the original question.
/// Score = mean cosine similarity (0–1, higher = more relevant answer).
/// </summary>
public sealed class AnswerRelevanceEvaluator(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    int syntheticQuestionCount = 3) : IRagasMetric
{
    public bool RequiresGroundTruth => false;

    public async Task<double> ScoreAsync(EvaluationSample sample, CancellationToken ct)
    {
        // Generate n synthetic questions from the predicted answer
        var questionTasks = Enumerable.Range(0, syntheticQuestionCount)
            .Select(_ => GenerateSyntheticQuestionAsync(sample.PredictedAnswer, ct));
        var syntheticQuestions = await Task.WhenAll(questionTasks).ConfigureAwait(false);

        // Embed original question + all synthetic questions in one batch
        var allTexts = new[] { sample.Question }.Concat(syntheticQuestions).ToList();
        var embeddings = await embeddingGenerator
            .GenerateAsync(allTexts, cancellationToken: ct)
            .ConfigureAwait(false);

        var originalEmbedding = embeddings[0].Vector.Span;

        // Mean cosine similarity of synthetic questions to original
        var similarities = new double[syntheticQuestionCount];
        for (var i = 0; i < syntheticQuestionCount; i++)
            similarities[i] = TensorPrimitives.CosineSimilarity(
                embeddings[i + 1].Vector.Span, originalEmbedding);

        return similarities.Average();
    }

    private async Task<string> GenerateSyntheticQuestionAsync(string answer, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Generate a single question that the following answer is responding to. " +
                "Output only the question, no explanation."),
            new(ChatRole.User, answer),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }
}
```

**Step 4: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "AnswerRelevanceEvaluatorTests" -v q
```

Expected: all 2 tests pass.

**Step 5: Run full suite + commit**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
git add src/Rag.NET.Evaluation.Ragas/AnswerRelevanceEvaluator.cs tests/Rag.NET.Tests/Evaluation/AnswerRelevanceEvaluatorTests.cs
git commit -m "feat(evaluation): add AnswerRelevanceEvaluator"
```

---

## Task 6: `ContextPrecisionEvaluator` — tests + implementation

**Files:**
- Create: `tests/Rag.NET.Tests/Evaluation/ContextPrecisionEvaluatorTests.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/ContextPrecisionEvaluator.cs`

**Spec:** For each source chunk, ask the LLM: "Is this chunk relevant to answering the question given the reference answer?" Score = relevant chunks / total chunks. Requires non-empty `ReferenceAnswer`.

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Evaluation/ContextPrecisionEvaluatorTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class ContextPrecisionEvaluatorTests
{
    [Fact]
    public async Task ScoreAsync_AllChunksRelevant_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["Chunk1", "Chunk2"]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_HalfChunksRelevant_ReturnsHalf()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "no")));

        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref.", ["ChunkA", "ChunkB"]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(0.5, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_EmptyReferenceAnswer_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextPrecisionEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", string.Empty, ["Chunk1"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken));
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "ContextPrecisionEvaluatorTests" -v q
```

**Step 3: Implement**

`src/Rag.NET.Evaluation.Ragas/ContextPrecisionEvaluator.cs`:
```csharp
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Context Precision: fraction of retrieved chunks that are relevant to the ground-truth answer.
/// Score = relevant_chunks / total_chunks (0–1, higher = more precise retrieval).
/// Requires a non-empty <see cref="EvaluationSample.ReferenceAnswer"/>.
/// </summary>
public sealed class ContextPrecisionEvaluator(IChatClient chatClient) : IRagasMetric
{
    public bool RequiresGroundTruth => true;

    public async Task<double> ScoreAsync(EvaluationSample sample, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sample.ReferenceAnswer))
            throw new InvalidOperationException(
                $"ContextPrecisionEvaluator requires a non-empty {nameof(EvaluationSample.ReferenceAnswer)}. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");

        var chunks = sample.SourceChunks;
        if (chunks is not { Count: > 0 })
            return 0.0;

        var tasks = chunks.Select(chunk => IsRelevantAsync(sample.Question, sample.ReferenceAnswer, chunk, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.Count(r => r) / (double)results.Length;
    }

    private async Task<bool> IsRelevantAsync(
        string question, string referenceAnswer, string chunk, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Answer only 'yes' or 'no': is the following context chunk useful for answering the question, " +
                "given the reference answer?"),
            new(ChatRole.User,
                new StringBuilder()
                    .AppendLine(CultureInfo.InvariantCulture, $"Question: {question}")
                    .AppendLine(CultureInfo.InvariantCulture, $"Reference Answer: {referenceAnswer}")
                    .AppendLine(CultureInfo.InvariantCulture, $"Context Chunk: {chunk}")
                    .ToString()),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var answer = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        return answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 4: Run tests + commit**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "ContextPrecisionEvaluatorTests" -v q
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
git add src/Rag.NET.Evaluation.Ragas/ContextPrecisionEvaluator.cs tests/Rag.NET.Tests/Evaluation/ContextPrecisionEvaluatorTests.cs
git commit -m "feat(evaluation): add ContextPrecisionEvaluator"
```

---

## Task 7: `ContextRecallEvaluator` — tests + implementation

**Files:**
- Create: `tests/Rag.NET.Tests/Evaluation/ContextRecallEvaluatorTests.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/ContextRecallEvaluator.cs`

**Spec:** Extract statements from the ground-truth `ReferenceAnswer` via LLM. For each statement, ask: "Is this statement supported by any of the source chunks?" Score = supported statements / total statements. Requires non-empty `ReferenceAnswer`.

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Evaluation/ContextRecallEvaluatorTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class ContextRecallEvaluatorTests
{
    [Fact]
    public async Task ScoreAsync_AllStatementsSupported_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                // Extract statements → 2 statements
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "[\"Stmt1\",\"Stmt2\"]")),
                // Stmt1 supported
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")),
                // Stmt2 supported
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));

        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Ref1. Ref2.", ["Chunk covering both."]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score, precision: 2);
    }

    [Fact]
    public async Task ScoreAsync_EmptyReferenceAnswer_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", string.Empty, ["Chunk1"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScoreAsync_NoStatementsExtracted_ReturnsOne()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var evaluator = new ContextRecallEvaluator(client);
        var sample = new EvaluationSample("Q?", "A.", "Short.", ["Chunk."]);

        var score = await evaluator.ScoreAsync(sample, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score); // no statements = trivially recalled
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "ContextRecallEvaluatorTests" -v q
```

**Step 3: Implement**

`src/Rag.NET.Evaluation.Ragas/ContextRecallEvaluator.cs`:
```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Context Recall: fraction of ground-truth statements supported by the retrieved chunks.
/// Score = supported_statements / total_statements (0–1, higher = better coverage).
/// Requires a non-empty <see cref="EvaluationSample.ReferenceAnswer"/>.
/// </summary>
public sealed class ContextRecallEvaluator(IChatClient chatClient) : IRagasMetric
{
    public bool RequiresGroundTruth => true;

    public async Task<double> ScoreAsync(EvaluationSample sample, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sample.ReferenceAnswer))
            throw new InvalidOperationException(
                $"ContextRecallEvaluator requires a non-empty {nameof(EvaluationSample.ReferenceAnswer)}. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");

        var statements = await ExtractStatementsAsync(sample.ReferenceAnswer, ct).ConfigureAwait(false);
        if (statements.Count == 0)
            return 1.0;

        var context = string.Join("\n", sample.SourceChunks ?? []);
        var tasks = statements.Select(stmt => IsSupportedAsync(stmt, context, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.Count(r => r) / (double)results.Length;
    }

    private async Task<IReadOnlyList<string>> ExtractStatementsAsync(string referenceAnswer, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Extract all atomic statements from the reference answer. " +
                "Output a JSON array of strings — one string per statement. No explanation."),
            new(ChatRole.User, referenceAnswer),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var raw = response.Messages.LastOrDefault()?.Text?.Trim() ?? "[]";
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<bool> IsSupportedAsync(string statement, string context, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Answer only 'yes' or 'no': is the following statement supported by the provided context?"),
            new(ChatRole.User,
                new StringBuilder()
                    .AppendLine(CultureInfo.InvariantCulture, $"Context: {context}")
                    .AppendLine(CultureInfo.InvariantCulture, $"Statement: {statement}")
                    .ToString()),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var answer = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        return answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }
}
```

**Step 4: Run tests + commit**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "ContextRecallEvaluatorTests" -v q
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
git add src/Rag.NET.Evaluation.Ragas/ContextRecallEvaluator.cs tests/Rag.NET.Tests/Evaluation/ContextRecallEvaluatorTests.cs
git commit -m "feat(evaluation): add ContextRecallEvaluator"
```

---

## Task 8: `RagasEvaluationSuite` — tests + implementation

**Files:**
- Create: `tests/Rag.NET.Tests/Evaluation/RagasEvaluationSuiteTests.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuiteBuilder.cs`
- Create: `src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuite.cs`

**Spec:** Fluent builder, registers metrics, `Build()` validates ground-truth requirements. `EvaluateAsync` runs all registered metrics concurrently per sample; samples processed sequentially. `RagasReport.OverallScore` = mean of registered (non-null) metrics.

**Step 1: Write failing tests**

`tests/Rag.NET.Tests/Evaluation/RagasEvaluationSuiteTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

public class RagasEvaluationSuiteTests
{
    private static EvaluationSample MakeSample(string referenceAnswer = "Ref.") =>
        new("Q?", "A.", referenceAnswer, ["Chunk."]);

    private static IChatClient AlwaysYesClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes")));
        return client;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> IdentityEmbedder()
    {
        var gen = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        gen.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var embeddings = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                    embeddings.Add(new Embedding<float>([1f, 0f]));
                return Task.FromResult(embeddings);
            });
        return gen;
    }

    [Fact]
    public async Task EvaluateAsync_SingleFaithfulnessMetric_ReturnsReport()
    {
        // FaithfulnessEvaluator: "yes" response → extract claims returns [] → score = 1.0
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]"))); // no claims → 1.0

        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddFaithfulness()
            .Build();

        var report = await suite.EvaluateAsync([MakeSample()], TestContext.Current.CancellationToken);

        Assert.NotNull(report.Faithfulness);
        Assert.Null(report.AnswerRelevance);
        Assert.Null(report.ContextPrecision);
        Assert.Null(report.ContextRecall);
        Assert.Equal(report.Faithfulness!.Value, report.OverallScore, precision: 2);
    }

    [Fact]
    public void Build_ContextPrecisionWithoutWarning_DoesNotThrow()
    {
        // Build() itself doesn't throw — validation happens at EvaluateAsync time
        var client = Substitute.For<IChatClient>();
        var builder = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddContextPrecision();

        var suite = builder.Build(); // should not throw
        Assert.NotNull(suite);
    }

    [Fact]
    public async Task EvaluateAsync_ContextPrecisionWithEmptyReferenceAnswer_Throws()
    {
        var client = Substitute.For<IChatClient>();
        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddContextPrecision()
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            suite.EvaluateAsync([MakeSample(referenceAnswer: "")], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvaluateAsync_OverallScoreIsMeanOfRegisteredMetrics()
    {
        // Mock all LLM responses to produce score = 1.0 from both metrics
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]"))); // faithfulness: no claims → 1.0

        var suite = new RagasEvaluationSuiteBuilder(client, IdentityEmbedder())
            .AddFaithfulness()
            .AddAnswerRelevance()
            .Build();

        var report = await suite.EvaluateAsync([MakeSample()], TestContext.Current.CancellationToken);

        Assert.NotNull(report.Faithfulness);
        Assert.NotNull(report.AnswerRelevance);
        var expected = (report.Faithfulness!.Value + report.AnswerRelevance!.Value) / 2.0;
        Assert.Equal(expected, report.OverallScore, precision: 2);
    }
}
```

**Step 2: Run to verify compile failure**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagasEvaluationSuiteTests" -v q
```

**Step 3: Implement builder**

`src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuiteBuilder.cs`:
```csharp
using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Fluent builder for <see cref="RagasEvaluationSuite"/>.
/// Register only the metrics you need — each adds LLM calls at evaluation time.
/// </summary>
public sealed class RagasEvaluationSuiteBuilder(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    private bool _faithfulness;
    private bool _answerRelevance;
    private bool _contextPrecision;
    private bool _contextRecall;

    public RagasEvaluationSuiteBuilder AddFaithfulness()    { _faithfulness    = true; return this; }
    public RagasEvaluationSuiteBuilder AddAnswerRelevance() { _answerRelevance = true; return this; }
    public RagasEvaluationSuiteBuilder AddContextPrecision(){ _contextPrecision = true; return this; }
    public RagasEvaluationSuiteBuilder AddContextRecall()   { _contextRecall   = true; return this; }

    /// <summary>
    /// Builds the suite. Validation of ground-truth requirements happens at
    /// <see cref="RagasEvaluationSuite.EvaluateAsync"/> time — fail fast on first
    /// sample with an empty ReferenceAnswer when Context Precision or Recall is registered.
    /// </summary>
    public RagasEvaluationSuite Build()
    {
        var metrics = new List<(string Name, IRagasMetric Metric)>();
        if (_faithfulness)    metrics.Add(("Faithfulness",     new FaithfulnessEvaluator(chatClient)));
        if (_answerRelevance) metrics.Add(("AnswerRelevance",  new AnswerRelevanceEvaluator(chatClient, embeddingGenerator)));
        if (_contextPrecision)metrics.Add(("ContextPrecision", new ContextPrecisionEvaluator(chatClient)));
        if (_contextRecall)   metrics.Add(("ContextRecall",    new ContextRecallEvaluator(chatClient)));

        return new RagasEvaluationSuite(metrics);
    }
}
```

**Step 4: Implement suite**

`src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuite.cs`:
```csharp
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Runs registered RAGAS metrics over a set of evaluation samples.
/// Metrics execute concurrently per sample; samples are processed sequentially.
/// </summary>
public sealed class RagasEvaluationSuite(
    IReadOnlyList<(string Name, IRagasMetric Metric)> metrics)
{
    public async Task<RagasReport> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));

        // Accumulate scores per metric across samples
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, _) in metrics)
            totals[name] = 0.0;

        foreach (var sample in samples)
        {
            // All metrics run concurrently per sample
            var tasks = metrics.Select(async m =>
                (m.Name, Score: await m.Metric.ScoreAsync(sample, cancellationToken).ConfigureAwait(false)));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var (name, score) in results)
                totals[name] += score;
        }

        double? faithfulness     = totals.TryGetValue("Faithfulness",    out var f) ? f / samples.Count : null;
        double? answerRelevance  = totals.TryGetValue("AnswerRelevance", out var a) ? a / samples.Count : null;
        double? contextPrecision = totals.TryGetValue("ContextPrecision",out var p) ? p / samples.Count : null;
        double? contextRecall    = totals.TryGetValue("ContextRecall",   out var r) ? r / samples.Count : null;

        var registered = new[] { faithfulness, answerRelevance, contextPrecision, contextRecall }
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var overallScore = registered.Count > 0 ? registered.Average() : 0.0;

        return new RagasReport
        {
            Faithfulness     = faithfulness,
            AnswerRelevance  = answerRelevance,
            ContextPrecision = contextPrecision,
            ContextRecall    = contextRecall,
            OverallScore     = overallScore,
        };
    }
}
```

**Step 5: Run tests**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj --filter "RagasEvaluationSuiteTests" -v q
```

Expected: all 4 tests pass.

**Step 6: Run full suite**

```
dotnet test tests/Rag.NET.Tests/Rag.NET.Tests.csproj -v q
```

Expected: all tests pass, no regressions.

**Step 7: Commit**

```bash
git add src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuiteBuilder.cs src/Rag.NET.Evaluation.Ragas/RagasEvaluationSuite.cs tests/Rag.NET.Tests/Evaluation/RagasEvaluationSuiteTests.cs
git commit -m "feat(evaluation): add RagasEvaluationSuiteBuilder and RagasEvaluationSuite"
```

---

## Task 9: Update features backlog

**Files:**
- Modify: `docs/reference/features.md`

**Step 1: Mark RAGAS-Style Metrics as done**

Find `### RAGAS-Style Metrics` and add before its closing `---`:
```markdown
**Status:** ✅ Done
```

**Step 2: Mark Evaluation Dataset Builder as done**

Find `### Evaluation Dataset Builder` and add before its closing `---`:
```markdown
**Status:** ✅ Done
```

**Step 3: Commit**

```bash
git add docs/reference/features.md
git commit -m "docs: mark RAGAS metrics and Evaluation Dataset Builder as done"
```
