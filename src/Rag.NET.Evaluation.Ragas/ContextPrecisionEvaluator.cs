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

    public async Task<double> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sample.ReferenceAnswer))
            throw new InvalidOperationException(
                $"ContextPrecisionEvaluator requires a non-empty {nameof(EvaluationSample.ReferenceAnswer)}. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");

        var chunks = sample.SourceChunks;
        if (chunks is not { Count: > 0 })
            return 0.0;

        var tasks = chunks.Select(chunk => IsRelevantAsync(sample.Question, sample.ReferenceAnswer, chunk, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.Count(r => r) / (double)results.Length;
    }

    private async Task<bool> IsRelevantAsync(
        string question, string referenceAnswer, string chunk, CancellationToken cancellationToken)
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
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        return answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }
}
