# LLM-as-Judge Evaluation Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `LlmJudgeEvaluator` to `Rag.NET.Evaluation` that scores RAG answers against named criteria (correctness, faithfulness, relevance) using a single `IChatClient` call per sample.

**Architecture:** One `IChatClient.GetResponseAsync` call per sample; all samples evaluated in parallel via `Task.WhenAll`. The LLM returns a JSON object with a score and reasoning string per criterion. Faithfulness is excluded from the prompt and result when no source chunks are provided. `EvaluationSample` gains an optional `IReadOnlyList<string>? SourceChunks` property (strings, not `SearchResult`, to keep the evaluation package free of a core `Rag.NET` dependency).

**Tech Stack:** `Microsoft.Extensions.AI` (`IChatClient`, `ChatMessage`, `ChatResponse`), `System.Text.Json`, xunit.v3, NSubstitute.

---

### Task 1: Extend `EvaluationSample` with optional `SourceChunks`

**Files:**
- Modify: `src/Rag.NET.Evaluation/EvaluationSample.cs`
- Test: `tests/Rag.NET.Evaluation.Tests/EmbeddingDistanceEvaluatorTests.cs` (verify existing tests still pass)

**Step 1: Add `SourceChunks` to `EvaluationSample`**

Open `src/Rag.NET.Evaluation/EvaluationSample.cs` and replace its contents:

```csharp
namespace Rag.NET.Evaluation;

/// <summary>A single question/answer pair to evaluate.</summary>
public sealed record EvaluationSample(
    string Question,
    string PredictedAnswer,
    string ReferenceAnswer,
    IReadOnlyList<string>? SourceChunks = null);
```

`SourceChunks` is the raw text of retrieved chunks (extracted from `SearchResult.Chunk.Text` by the caller). When null or empty, the faithfulness criterion is excluded from evaluation. Callers with `RagResponse` do: `response.Sources.Select(s => s.Chunk.Text).ToList()`.

**Step 2: Run the existing evaluation tests to confirm backward compatibility**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: all 5 tests pass (the 4-arg constructor with `SourceChunks = null` is default).

**Step 3: Commit**

```bash
git add src/Rag.NET.Evaluation/EvaluationSample.cs
git commit -m "feat: add optional SourceChunks to EvaluationSample"
```

---

### Task 2: Add `JudgeCriterion` and `LlmJudgeException`

**Files:**
- Create: `src/Rag.NET.Evaluation/JudgeCriterion.cs`
- Create: `src/Rag.NET.Evaluation/LlmJudgeException.cs`
- Create: `tests/Rag.NET.Evaluation.Tests/JudgeCriterionTests.cs`

**Step 1: Write the failing test**

Create `tests/Rag.NET.Evaluation.Tests/JudgeCriterionTests.cs`:

```csharp
using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class JudgeCriterionTests
{
    [Fact]
    public void Correctness_HasExpectedName()
        => Assert.Equal("correctness", JudgeCriterion.Correctness.Name);

    [Fact]
    public void Faithfulness_HasExpectedName()
        => Assert.Equal("faithfulness", JudgeCriterion.Faithfulness.Name);

    [Fact]
    public void Relevance_HasExpectedName()
        => Assert.Equal("relevance", JudgeCriterion.Relevance.Name);

    [Fact]
    public void AllDefaults_HaveNonEmptyDescriptions()
    {
        Assert.NotEmpty(JudgeCriterion.Correctness.Description);
        Assert.NotEmpty(JudgeCriterion.Faithfulness.Description);
        Assert.NotEmpty(JudgeCriterion.Relevance.Description);
    }
}
```

**Step 2: Run to confirm failure**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: compile error — `JudgeCriterion` does not exist.

**Step 3: Create `JudgeCriterion.cs`**

```csharp
namespace Rag.NET.Evaluation;

/// <summary>A named evaluation criterion with a rubric description for the LLM judge.</summary>
public sealed record JudgeCriterion(string Name, string Description)
{
    public static readonly JudgeCriterion Correctness = new(
        "correctness",
        "Is the predicted answer factually correct given the reference answer?");

    public static readonly JudgeCriterion Faithfulness = new(
        "faithfulness",
        "Does the predicted answer stay grounded in the retrieved context without hallucinating facts not present in the context?");

    public static readonly JudgeCriterion Relevance = new(
        "relevance",
        "Does the predicted answer directly and completely address the question?");
}
```

**Step 4: Create `LlmJudgeException.cs`**

```csharp
namespace Rag.NET.Evaluation;

/// <summary>
/// Thrown when the LLM judge returns a response that cannot be parsed
/// or is missing required criteria.
/// </summary>
public sealed class LlmJudgeException(string message, string rawResponse)
    : Exception(message)
{
    /// <summary>The raw response text from the LLM, for diagnosis.</summary>
    public string RawResponse { get; } = rawResponse;
}
```

**Step 5: Run tests to confirm pass**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: all tests pass.

**Step 6: Commit**

```bash
git add src/Rag.NET.Evaluation/JudgeCriterion.cs src/Rag.NET.Evaluation/LlmJudgeException.cs tests/Rag.NET.Evaluation.Tests/JudgeCriterionTests.cs
git commit -m "feat: add JudgeCriterion defaults and LlmJudgeException"
```

---

### Task 3: Add result types with convenience methods

**Files:**
- Create: `src/Rag.NET.Evaluation/CriterionScore.cs`
- Create: `src/Rag.NET.Evaluation/SampleJudgement.cs`
- Create: `src/Rag.NET.Evaluation/LlmJudgeResult.cs`
- Create: `tests/Rag.NET.Evaluation.Tests/LlmJudgeResultTests.cs`

**Step 1: Write the failing tests**

Create `tests/Rag.NET.Evaluation.Tests/LlmJudgeResultTests.cs`:

```csharp
using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class LlmJudgeResultTests
{
    private static LlmJudgeResult MakeResult(params (string criterion, double score)[] perSample)
    {
        var judgements = new List<SampleJudgement>();
        foreach (var (criterion, score) in perSample)
        {
            judgements.Add(new SampleJudgement(
                Question: "q",
                Criteria: new Dictionary<string, CriterionScore>
                {
                    [criterion] = new CriterionScore(score, "reason"),
                }));
        }
        return new LlmJudgeResult(judgements);
    }

    [Fact]
    public void MeanScore_AveragesAcrossSamples()
    {
        var result = MakeResult(("correctness", 0.8), ("correctness", 0.6));
        Assert.Equal(0.7, result.MeanScore("correctness"), precision: 10);
    }

    [Fact]
    public void MeanScore_WhenCriterionAbsent_ReturnsZero()
    {
        var result = MakeResult(("correctness", 0.8));
        Assert.Equal(0.0, result.MeanScore("relevance"), precision: 10);
    }

    [Fact]
    public void AllPass_WhenAllMeetThreshold_ReturnsTrue()
    {
        var result = MakeResult(("correctness", 0.8), ("correctness", 0.9));
        Assert.True(result.AllPass("correctness", 0.7));
    }

    [Fact]
    public void AllPass_WhenOneFails_ReturnsFalse()
    {
        var result = MakeResult(("correctness", 0.8), ("correctness", 0.5));
        Assert.False(result.AllPass("correctness", 0.7));
    }
}
```

**Step 2: Run to confirm failure**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: compile errors — types do not exist yet.

**Step 3: Create `CriterionScore.cs`**

```csharp
namespace Rag.NET.Evaluation;

/// <summary>Score and reasoning for a single criterion on a single sample.</summary>
public sealed record CriterionScore(double Score, string Reasoning);
```

**Step 4: Create `SampleJudgement.cs`**

```csharp
namespace Rag.NET.Evaluation;

/// <summary>All criteria scores for a single evaluated sample.</summary>
public sealed record SampleJudgement(
    string Question,
    IReadOnlyDictionary<string, CriterionScore> Criteria);
```

**Step 5: Create `LlmJudgeResult.cs`**

```csharp
namespace Rag.NET.Evaluation;

/// <summary>
/// Result of an LLM judge evaluation run.
/// Contains per-criterion scores and reasoning for each sample.
/// </summary>
public sealed record LlmJudgeResult(IReadOnlyList<SampleJudgement> Samples)
{
    /// <summary>
    /// Returns the mean score across all samples for the given criterion.
    /// Returns 0.0 if no sample contains that criterion.
    /// </summary>
    public double MeanScore(string criterion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterion);
        var scores = Samples
            .Where(s => s.Criteria.ContainsKey(criterion))
            .Select(s => s.Criteria[criterion].Score)
            .ToList();
        return scores.Count == 0 ? 0.0 : scores.Average();
    }

    /// <summary>
    /// Returns true if every sample that contains the criterion meets the threshold.
    /// Returns true if no sample contains that criterion (vacuously).
    /// </summary>
    public bool AllPass(string criterion, double threshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterion);
        return Samples
            .Where(s => s.Criteria.ContainsKey(criterion))
            .All(s => s.Criteria[criterion].Score >= threshold);
    }
}
```

**Step 6: Run tests to confirm pass**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: all tests pass.

**Step 7: Commit**

```bash
git add src/Rag.NET.Evaluation/CriterionScore.cs src/Rag.NET.Evaluation/SampleJudgement.cs src/Rag.NET.Evaluation/LlmJudgeResult.cs tests/Rag.NET.Evaluation.Tests/LlmJudgeResultTests.cs
git commit -m "feat: add LlmJudgeResult, SampleJudgement, CriterionScore result types"
```

---

### Task 4: Implement `LlmJudgeEvaluator` — happy path

**Files:**
- Create: `src/Rag.NET.Evaluation/LlmJudgeEvaluator.cs`
- Create: `tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs`

**Step 1: Write the failing test**

Create `tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Evaluation;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

public class LlmJudgeEvaluatorTests
{
    private const string ValidJsonAllCriteria = """
        {
          "correctness":  { "score": 0.85, "reasoning": "Mostly correct." },
          "faithfulness": { "score": 0.90, "reasoning": "Grounded in context." },
          "relevance":    { "score": 1.00, "reasoning": "Directly answers." }
        }
        """;

    private const string ValidJsonTwoCriteria = """
        {
          "correctness": { "score": 0.75, "reasoning": "Partially correct." },
          "relevance":   { "score": 0.95, "reasoning": "Relevant." }
        }
        """;

    private static IChatClient MakeChatClient(string jsonResponse)
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)]));
        return client;
    }

    [Fact]
    public async Task EvaluateAsync_WithSources_ReturnsAllThreeCriteria()
    {
        var client = MakeChatClient(ValidJsonAllCriteria);
        var sut = new LlmJudgeEvaluator(client);

        var samples = new[]
        {
            new EvaluationSample(
                Question: "What is RAG?",
                PredictedAnswer: "RAG is retrieval-augmented generation.",
                ReferenceAnswer: "RAG combines retrieval with LLM generation.",
                SourceChunks: ["Context chunk 1", "Context chunk 2"]),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        var judgement = Assert.Single(result.Samples);
        Assert.Equal("What is RAG?", judgement.Question);
        Assert.True(judgement.Criteria.ContainsKey("correctness"));
        Assert.True(judgement.Criteria.ContainsKey("faithfulness"));
        Assert.True(judgement.Criteria.ContainsKey("relevance"));
        Assert.Equal(0.85, judgement.Criteria["correctness"].Score, precision: 10);
        Assert.Equal("Mostly correct.", judgement.Criteria["correctness"].Reasoning);
    }

    [Fact]
    public async Task EvaluateAsync_MultipleSamples_ReturnsOneJudgementPerSample()
    {
        var client = Substitute.For<IChatClient>();
        client
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJsonTwoCriteria)]),
                new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJsonTwoCriteria)]));

        var sut = new LlmJudgeEvaluator(client);
        var samples = new[]
        {
            new EvaluationSample("Q1", "A1", "R1"),
            new EvaluationSample("Q2", "A2", "R2"),
        };

        var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Samples.Count);
    }
}
```

**Step 2: Run to confirm failure**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs -v minimal
```

Expected: compile error — `LlmJudgeEvaluator` does not exist.

**Step 3: Create `LlmJudgeEvaluator.cs`**

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation;

/// <summary>
/// Evaluates RAG answer quality using an LLM as judge.
/// Issues one <see cref="IChatClient"/> call per sample (all in parallel).
/// Returns per-criterion scores (0–1) and reasoning text.
/// </summary>
public sealed class LlmJudgeEvaluator(
    IChatClient chatClient,
    IReadOnlyList<JudgeCriterion>? criteria = null)
{
    private static readonly IReadOnlyList<JudgeCriterion> DefaultCriteria =
    [
        JudgeCriterion.Correctness,
        JudgeCriterion.Faithfulness,
        JudgeCriterion.Relevance,
    ];

    private readonly IReadOnlyList<JudgeCriterion> _criteria = criteria ?? DefaultCriteria;

    private const string SystemMessage =
        "You are an expert evaluator of RAG system outputs. " +
        "Score the predicted answer against each criterion on a scale of 0.0 to 1.0. " +
        "Respond with valid JSON only — no markdown, no explanation outside the JSON.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<LlmJudgeResult> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));

        var tasks = samples.Select(s => EvaluateSampleAsync(s, cancellationToken));
        var judgements = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new LlmJudgeResult(judgements);
    }

    private async Task<SampleJudgement> EvaluateSampleAsync(
        EvaluationSample sample,
        CancellationToken ct)
    {
        var hasSources = sample.SourceChunks is { Count: > 0 };

        var activeCriteria = _criteria
            .Where(c => c.Name != JudgeCriterion.Faithfulness.Name || hasSources)
            .ToList();

        var userMessage = BuildUserMessage(sample, activeCriteria, hasSources);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemMessage),
            new(ChatRole.User, userMessage),
        };

        var response = await chatClient
            .GetResponseAsync(messages, cancellationToken: ct)
            .ConfigureAwait(false);

        var rawText = response.Messages.LastOrDefault()?.Text ?? string.Empty;
        var criteriaScores = ParseResponse(rawText, activeCriteria);
        return new SampleJudgement(sample.Question, criteriaScores);
    }

    private static string BuildUserMessage(
        EvaluationSample sample,
        IReadOnlyList<JudgeCriterion> activeCriteria,
        bool hasSources)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Question: {sample.Question}");
        sb.AppendLine($"Predicted Answer: {sample.PredictedAnswer}");
        sb.AppendLine($"Reference Answer: {sample.ReferenceAnswer}");

        if (hasSources)
        {
            sb.AppendLine("Retrieved Context:");
            for (int i = 0; i < sample.SourceChunks!.Count; i++)
                sb.AppendLine($"  [{i + 1}] {sample.SourceChunks[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("Evaluate against these criteria:");
        foreach (var c in activeCriteria)
            sb.AppendLine($"- {c.Name}: {c.Description}");

        sb.AppendLine();
        sb.AppendLine("Respond with this exact JSON shape:");
        sb.Append('{');
        sb.AppendLine();
        foreach (var c in activeCriteria)
            sb.AppendLine($"  \"{c.Name}\": {{ \"score\": 0.0, \"reasoning\": \"...\" }},");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, CriterionScore> ParseResponse(
        string rawText,
        IReadOnlyList<JudgeCriterion> activeCriteria)
    {
        var json = rawText.Trim();

        // Strip markdown code fence if present (```json ... ``` or ``` ... ```)
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = json.IndexOf('\n');
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                json = json[(firstNewline + 1)..lastFence].Trim();
        }

        Dictionary<string, CriterionDto>? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dictionary<string, CriterionDto>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new LlmJudgeException(
                $"Failed to parse LLM judge response as JSON: {ex.Message}", rawText);
        }

        if (dto is null)
            throw new LlmJudgeException("LLM judge returned null JSON.", rawText);

        var result = new Dictionary<string, CriterionScore>(StringComparer.OrdinalIgnoreCase);
        foreach (var criterion in activeCriteria)
        {
            if (!dto.TryGetValue(criterion.Name, out var entry))
                throw new LlmJudgeException(
                    $"LLM judge response missing criterion '{criterion.Name}'.", rawText);

            var score = Math.Clamp(entry.Score, 0.0, 1.0);
            result[criterion.Name] = new CriterionScore(score, entry.Reasoning ?? string.Empty);
        }

        return result;
    }

    private sealed record CriterionDto(
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("reasoning")] string? Reasoning);
}
```

**Step 4: Run tests to confirm pass**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET.Evaluation/LlmJudgeEvaluator.cs tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs
git commit -m "feat: implement LlmJudgeEvaluator happy path"
```

---

### Task 5: Sources-null path — faithfulness excluded

**Files:**
- Modify: `tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs`

**Step 1: Add the test** (append to the existing test class):

```csharp
[Fact]
public async Task EvaluateAsync_WithoutSources_FaithfulnessAbsentFromResult()
{
    var client = MakeChatClient(ValidJsonTwoCriteria);
    var sut = new LlmJudgeEvaluator(client);

    var samples = new[]
    {
        new EvaluationSample(
            Question: "What is RAG?",
            PredictedAnswer: "RAG is retrieval-augmented generation.",
            ReferenceAnswer: "RAG combines retrieval with LLM generation."),
            // SourceChunks omitted (null)
    };

    var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

    var judgement = Assert.Single(result.Samples);
    Assert.False(judgement.Criteria.ContainsKey("faithfulness"));
    Assert.True(judgement.Criteria.ContainsKey("correctness"));
    Assert.True(judgement.Criteria.ContainsKey("relevance"));
}

[Fact]
public async Task EvaluateAsync_WithoutSources_PromptDoesNotMentionFaithfulness()
{
    var client = Substitute.For<IChatClient>();
    IEnumerable<ChatMessage>? capturedMessages = null;
    client
        .GetResponseAsync(
            Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>())
        .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, ValidJsonTwoCriteria)]));

    var sut = new LlmJudgeEvaluator(client);
    var samples = new[] { new EvaluationSample("Q", "A", "R") };

    await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

    var userMessage = capturedMessages!.Last().Text ?? string.Empty;
    Assert.DoesNotContain("faithfulness", userMessage, StringComparison.OrdinalIgnoreCase);
}
```

**Step 2: Run to confirm the tests pass** (the implementation already handles this):

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: all tests pass — the existing implementation already excludes faithfulness when `SourceChunks` is null.

**Step 3: Commit**

```bash
git add tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs
git commit -m "test: verify faithfulness excluded when SourceChunks is null"
```

---

### Task 6: Error paths, fence stripping, and score clamping

**Files:**
- Modify: `tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs`

**Step 1: Add the tests** (append to the existing test class):

```csharp
[Fact]
public async Task EvaluateAsync_EmptySamples_ThrowsArgumentException()
{
    var sut = new LlmJudgeEvaluator(Substitute.For<IChatClient>());

    await Assert.ThrowsAsync<ArgumentException>(
        () => sut.EvaluateAsync([], TestContext.Current.CancellationToken));
}

[Fact]
public async Task EvaluateAsync_MalformedJson_ThrowsLlmJudgeExceptionWithRawResponse()
{
    var client = MakeChatClient("this is not json at all");
    var sut = new LlmJudgeEvaluator(client);
    var samples = new[] { new EvaluationSample("Q", "A", "R") };

    var ex = await Assert.ThrowsAsync<LlmJudgeException>(
        () => sut.EvaluateAsync(samples, TestContext.Current.CancellationToken));

    Assert.Contains("this is not json at all", ex.RawResponse);
}

[Fact]
public async Task EvaluateAsync_FencedJson_ParsedSuccessfully()
{
    var fencedJson = "```json\n" + ValidJsonTwoCriteria + "\n```";
    var client = MakeChatClient(fencedJson);
    var sut = new LlmJudgeEvaluator(client);
    var samples = new[] { new EvaluationSample("Q", "A", "R") };

    var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

    var judgement = Assert.Single(result.Samples);
    Assert.Equal(0.75, judgement.Criteria["correctness"].Score, precision: 10);
}

[Fact]
public async Task EvaluateAsync_MissingCriterionInResponse_ThrowsLlmJudgeException()
{
    // Response only has correctness, but evaluator also expects relevance
    const string incompleteJson = """{ "correctness": { "score": 0.8, "reasoning": "ok" } }""";
    var client = MakeChatClient(incompleteJson);
    var sut = new LlmJudgeEvaluator(client);
    var samples = new[] { new EvaluationSample("Q", "A", "R") };

    await Assert.ThrowsAsync<LlmJudgeException>(
        () => sut.EvaluateAsync(samples, TestContext.Current.CancellationToken));
}

[Fact]
public async Task EvaluateAsync_ScoreAboveOne_ClampedToOne()
{
    const string outOfRangeJson = """
        {
          "correctness": { "score": 1.5, "reasoning": "great" },
          "relevance":   { "score": 0.9, "reasoning": "relevant" }
        }
        """;
    var client = MakeChatClient(outOfRangeJson);
    var sut = new LlmJudgeEvaluator(client);
    var samples = new[] { new EvaluationSample("Q", "A", "R") };

    var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

    Assert.Equal(1.0, result.Samples[0].Criteria["correctness"].Score, precision: 10);
}

[Fact]
public async Task EvaluateAsync_ScoreBelowZero_ClampedToZero()
{
    const string outOfRangeJson = """
        {
          "correctness": { "score": -0.3, "reasoning": "bad" },
          "relevance":   { "score": 0.9,  "reasoning": "relevant" }
        }
        """;
    var client = MakeChatClient(outOfRangeJson);
    var sut = new LlmJudgeEvaluator(client);
    var samples = new[] { new EvaluationSample("Q", "A", "R") };

    var result = await sut.EvaluateAsync(samples, TestContext.Current.CancellationToken);

    Assert.Equal(0.0, result.Samples[0].Criteria["correctness"].Score, precision: 10);
}
```

**Step 2: Run to confirm all pass**

```bash
dotnet test tests/Rag.NET.Evaluation.Tests/ -v minimal
```

Expected: all tests pass — the implementation already handles these cases.

**Step 3: Run full test suite to confirm no regressions**

```bash
dotnet test -c Release --no-restore -v minimal
```

Expected: all tests pass.

**Step 4: Commit**

```bash
git add tests/Rag.NET.Evaluation.Tests/LlmJudgeEvaluatorTests.cs
git commit -m "test: add error paths, fence stripping, and score clamping tests"
```

---

### Task 7: Update evaluation guide docs

**Files:**
- Modify: `docs/guide/evaluation.md`

**Step 1: Add `LlmJudgeEvaluator` section to `docs/guide/evaluation.md`**

Append the following sections after the existing "Limitations" section:

```markdown
---

## `LlmJudgeEvaluator`

Uses an `IChatClient` to grade answers against named criteria. One LLM call per sample; all samples evaluated in parallel. Returns scores and reasoning text per criterion.

```csharp
using Microsoft.Extensions.AI;
using Rag.NET.Evaluation;

var judge = new LlmJudgeEvaluator(chatClient);
```

The default criteria are **correctness**, **faithfulness**, and **relevance**. Pass a custom list to restrict or extend them:

```csharp
var judge = new LlmJudgeEvaluator(chatClient, criteria:
[
    JudgeCriterion.Correctness,
    JudgeCriterion.Relevance,
]);
```

### Providing source chunks

Pass `SourceChunks` to enable the **faithfulness** criterion. Without it, faithfulness is automatically excluded:

```csharp
var response = await pipeline.AskAsync("What is RAG?");

var samples = new[]
{
    new EvaluationSample(
        Question:        "What is RAG?",
        PredictedAnswer: response.Answer,
        ReferenceAnswer: "RAG combines retrieval with LLM generation.",
        SourceChunks:    response.Sources.Select(s => s.Chunk.Text).ToList()),
};

var result = await judge.EvaluateAsync(samples);
```

### Reading results

```csharp
// Mean score per criterion across all samples
double correctnessMean = result.MeanScore("correctness");   // e.g. 0.83
double faithfulnessMean = result.MeanScore("faithfulness"); // e.g. 0.91

// Per-sample reasoning
foreach (var judgement in result.Samples)
{
    Console.WriteLine($"Q: {judgement.Question}");
    foreach (var (criterion, score) in judgement.Criteria)
        Console.WriteLine($"  {criterion}: {score.Score:F2} — {score.Reasoning}");
}
```

### CI gate

```csharp
Assert.True(result.AllPass("correctness", threshold: 0.7),
    $"Correctness regression: mean {result.MeanScore("correctness"):F2}");
```

### `JudgeCriterion`

```csharp
public sealed record JudgeCriterion(string Name, string Description)
{
    public static readonly JudgeCriterion Correctness;   // factual accuracy vs reference
    public static readonly JudgeCriterion Faithfulness;  // grounded in context (requires SourceChunks)
    public static readonly JudgeCriterion Relevance;     // answers the question
}
```

Create custom criteria by constructing a `JudgeCriterion` with your own name and rubric description:

```csharp
var conciseness = new JudgeCriterion(
    "conciseness",
    "Is the answer concise and free of unnecessary padding?");

var judge = new LlmJudgeEvaluator(chatClient, criteria: [conciseness]);
```

### Errors

`LlmJudgeException` is thrown when the LLM returns malformed JSON or omits a required criterion. The `RawResponse` property contains the raw LLM output for diagnosis.
```

**Step 2: Commit**

```bash
git add docs/guide/evaluation.md
git commit -m "docs: add LlmJudgeEvaluator documentation"
```
