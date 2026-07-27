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

    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sample.ReferenceAnswer))
            throw new InvalidOperationException(
                $"ContextRecallEvaluator requires a non-empty {nameof(EvaluationSample.ReferenceAnswer)}. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");

        if (sample.SourceChunks is not { Count: > 0 })
            return 0.0;

        var statements = await ExtractStatementsAsync(sample.ReferenceAnswer, cancellationToken).ConfigureAwait(false);
        if (statements.Count == 0)
            return 1.0;

        var context = string.Join("\n", sample.SourceChunks);
        var tasks = statements.Select(stmt => IsSupportedAsync(stmt, context, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.Count(r => r) / (double)results.Length;
    }

    private async Task<IReadOnlyList<string>> ExtractStatementsAsync(string referenceAnswer, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Extract all atomic statements from the reference answer. " +
                "Output a JSON array of strings — one string per statement. No explanation."),
            new(ChatRole.User, referenceAnswer),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = response.Messages.LastOrDefault()?.Text?.Trim() ?? "[]";
        try { return JsonSerializer.Deserialize(raw, RagJsonSerializerContext.Default.ListString) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<bool> IsSupportedAsync(string statement, string context, CancellationToken cancellationToken)
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
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        return answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }
}
