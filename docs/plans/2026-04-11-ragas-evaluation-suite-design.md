# RAGAS Evaluation Suite — Design

## Goal

Add a complete RAGAS-style evaluation suite to Rag.NET, split across two packages:

- **`Rag.NET.Evaluation`** — already exists; owns `IRagEvaluator`, `EvaluationSample`, `LlmJudgeEvaluator`. Extended with `EvaluationDatasetBuilder`.
- **`Rag.NET.Evaluation.Ragas`** — new package; owns the four RAGAS metrics and the `RagasEvaluationSuite` fluent builder.

This split avoids pulling `IEmbeddingGenerator` into `Rag.NET.Evaluation` for users who only need `LlmJudgeEvaluator`.

---

## Key Decisions

| Question | Decision |
|---|---|
| Answer Relevance scoring | Embedding similarity (generate `n=3` synthetic questions, cosine to original query) — matches published RAGAS methodology, avoids LLM self-evaluation |
| Suite API | Fluent builder — pay only for metrics you register |
| Ground-truth validation | `EvaluateAsync` throws `InvalidOperationException` on first sample with empty `ReferenceAnswer` when Context Precision or Recall is registered — fail fast |
| Dataset Builder modes | `QuestionOnly` (1 LLM call/sample) or `QuestionAndAnswer` (2 calls/sample), configurable via `EvaluationDatasetBuilderOptions` |
| Concurrency | Metrics run concurrently per sample; samples processed sequentially to avoid rate-limit hammering |

---

## Package: `Rag.NET.Evaluation` — additions

### `EvaluationDatasetBuilder`

```csharp
public enum DatasetGenerationMode { QuestionOnly, QuestionAndAnswer }

public sealed class EvaluationDatasetBuilderOptions
{
    public int SampleCount            { get; init; } = 20;
    public DatasetGenerationMode Mode { get; init; } = DatasetGenerationMode.QuestionOnly;
}

public sealed class EvaluationDatasetBuilder(
    IVectorStore vectorStore,
    IChatClient chatClient)
{
    public Task<IReadOnlyList<EvaluationSample>> BuildAsync(
        EvaluationDatasetBuilderOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

**Behaviour:**
- Fetches all chunks from `IVectorStore` (via `IRagDataManager.GetChunksAsync` or equivalent); randomly samples `SampleCount`
- Per chunk: one LLM call to generate a question grounded in that chunk text
- If `QuestionAndAnswer`: second LLM call generates a reference answer grounded in the same chunk
- `QuestionOnly` produces `ReferenceAnswer = ""` — caller must fill it in or avoid metrics that require it
- All per-chunk LLM calls run concurrently

**Dependency:** adds `IRagDataManager` (already in `Rag.NET.Abstractions`) to `Rag.NET.Evaluation.csproj`. No new NuGet packages.

---

## Package: `Rag.NET.Evaluation.Ragas` — new

### Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>Rag.NET.Evaluation.Ragas</PackageId>
    <Description>RAGAS-style evaluation metrics for Rag.NET</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Rag.NET.Evaluation\Rag.NET.Evaluation.csproj" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
  </ItemGroup>
</Project>
```

### `RagasReport`

```csharp
public sealed record RagasReport
{
    public double? Faithfulness     { get; init; }  // null = not registered
    public double? AnswerRelevance  { get; init; }
    public double? ContextPrecision { get; init; }
    public double? ContextRecall    { get; init; }
    public double OverallScore      { get; init; }  // mean of registered (non-null) metrics
}
```

### Individual evaluators

Each implements a common internal interface used by the suite:

```csharp
internal interface IRagasMetric
{
    bool RequiresGroundTruth { get; }
    Task<double> ScoreAsync(EvaluationSample sample, CancellationToken ct);
}
```

| Evaluator | Constructor deps | `RequiresGroundTruth` | LLM calls |
|---|---|---|---|
| `FaithfulnessEvaluator` | `IChatClient` | false | Extract atomic claims → verify each against chunks |
| `AnswerRelevanceEvaluator` | `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>` | false | Generate 3 questions → embed → cosine to original query |
| `ContextPrecisionEvaluator` | `IChatClient` | true | Classify each chunk relevant/irrelevant to ground-truth |
| `ContextRecallEvaluator` | `IChatClient` | true | Map ground-truth statements to supporting chunks |

### `RagasEvaluationSuiteBuilder` + `RagasEvaluationSuite`

```csharp
public sealed class RagasEvaluationSuiteBuilder(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    public RagasEvaluationSuiteBuilder AddFaithfulness();
    public RagasEvaluationSuiteBuilder AddAnswerRelevance();
    public RagasEvaluationSuiteBuilder AddContextPrecision();
    public RagasEvaluationSuiteBuilder AddContextRecall();

    /// <summary>
    /// Builds the suite. Throws <see cref="InvalidOperationException"/> if
    /// Context Precision or Recall was registered but no ground-truth validation
    /// can be done at this point — validation happens at EvaluateAsync time.
    /// </summary>
    public RagasEvaluationSuite Build();
}

public sealed class RagasEvaluationSuite
{
    /// <summary>
    /// Evaluates all samples. Throws <see cref="InvalidOperationException"/> on the
    /// first sample with an empty ReferenceAnswer when a ground-truth metric is registered.
    /// All registered metrics run concurrently per sample; samples processed sequentially.
    /// </summary>
    public Task<RagasReport> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default);
}
```

---

## Usage Example

```csharp
// 1. Build a synthetic dataset
var builder = new EvaluationDatasetBuilder(vectorStore, chatClient);
var samples = await builder.BuildAsync(new EvaluationDatasetBuilderOptions
{
    SampleCount = 50,
    Mode = DatasetGenerationMode.QuestionAndAnswer,
});

// 2. Run your pipeline to get predicted answers
var evaluated = await Task.WhenAll(samples.Select(async s =>
{
    var result = await pipeline.AskAsync(s.Question);
    return s with { PredictedAnswer = result.Answer };
}));

// 3. Score with RAGAS suite
var suite = new RagasEvaluationSuiteBuilder(chatClient, embeddingGenerator)
    .AddFaithfulness()
    .AddAnswerRelevance()
    .AddContextPrecision()
    .AddContextRecall()
    .Build();

RagasReport report = await suite.EvaluateAsync(evaluated);
Console.WriteLine($"Overall: {report.OverallScore:P0}  Faithfulness: {report.Faithfulness:P0}");
```

---

## Testing

Each evaluator gets its own test class in `tests/Rag.NET.Tests/Evaluation/`:

- `EvaluationDatasetBuilderTests` — mock `IVectorStore` + `IChatClient`; verify sample count, mode switch, concurrent LLM calls
- `FaithfulnessEvaluatorTests` — mock `IChatClient`; verify claim extraction and verification prompt structure
- `AnswerRelevanceEvaluatorTests` — mock `IChatClient` + `IEmbeddingGenerator`; verify `n=3` synthetic questions, cosine scoring
- `ContextPrecisionEvaluatorTests` — mock `IChatClient`; verify ground-truth validation throw, precision calculation
- `ContextRecallEvaluatorTests` — mock `IChatClient`; verify ground-truth validation throw, recall calculation
- `RagasEvaluationSuiteTests` — verify fluent builder, concurrent per-sample scoring, `RagasReport.OverallScore` = mean of registered metrics

---

## File Map

```
src/
  Rag.NET.Evaluation/
    EvaluationDatasetBuilder.cs        ← new
    EvaluationDatasetBuilderOptions.cs ← new
  Rag.NET.Evaluation.Ragas/
    Rag.NET.Evaluation.Ragas.csproj    ← new
    IRagasMetric.cs                    ← new (internal)
    FaithfulnessEvaluator.cs           ← new
    AnswerRelevanceEvaluator.cs        ← new
    ContextPrecisionEvaluator.cs       ← new
    ContextRecallEvaluator.cs          ← new
    RagasReport.cs                     ← new
    RagasEvaluationSuiteBuilder.cs     ← new
    RagasEvaluationSuite.cs            ← new

tests/Rag.NET.Tests/Evaluation/
    EvaluationDatasetBuilderTests.cs   ← new
    FaithfulnessEvaluatorTests.cs      ← new
    AnswerRelevanceEvaluatorTests.cs   ← new
    ContextPrecisionEvaluatorTests.cs  ← new
    ContextRecallEvaluatorTests.cs     ← new
    RagasEvaluationSuiteTests.cs       ← new
```
