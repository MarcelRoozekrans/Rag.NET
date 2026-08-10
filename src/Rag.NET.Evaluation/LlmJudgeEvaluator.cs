using System.Globalization;
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
            .Where(c => !string.Equals(c.Name, JudgeCriterion.Faithfulness.Name, StringComparison.Ordinal) || hasSources)
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
        sb.Append(CultureInfo.InvariantCulture, $"Question: {sample.Question}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Predicted Answer: {sample.PredictedAnswer}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Reference Answer: {sample.ReferenceAnswer}").AppendLine();

        if (hasSources)
        {
            sb.AppendLine("Retrieved Context:");
            for (int i = 0; i < sample.SourceChunks!.Count; i++)
                sb.Append(CultureInfo.InvariantCulture, $"  [{i + 1}] {sample.SourceChunks[i]}").AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Evaluate against these criteria:");
        foreach (var c in activeCriteria)
            sb.Append(CultureInfo.InvariantCulture, $"- {c.Name}: {c.Description}").AppendLine();

        sb.AppendLine();
        sb.AppendLine("Respond with this exact JSON shape:");
        sb.AppendLine("{");
        for (var i = 0; i < activeCriteria.Count; i++)
        {
            var comma = i < activeCriteria.Count - 1 ? "," : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"  \"{activeCriteria[i].Name}\": {{ \"score\": 0.0, \"reasoning\": \"...\" }}{comma}").AppendLine();
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, CriterionScore> ParseResponse(
        string rawText,
        IReadOnlyList<JudgeCriterion> activeCriteria)
    {
        // The local strip this replaces ran only when the response STARTED with a fence; a
        // preamble made every judgement throw LlmJudgeException — loud, but wrong, because the
        // JSON was right there behind the prose.
        var json = LlmJsonExtractor.Extract(rawText, LlmJsonPayloadKind.Object);

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
