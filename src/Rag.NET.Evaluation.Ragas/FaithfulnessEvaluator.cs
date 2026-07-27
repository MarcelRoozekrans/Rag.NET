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

    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        if (sample.SourceChunks is not { Count: > 0 })
            return 0.0;

        var claims = await ExtractClaimsAsync(sample.PredictedAnswer, cancellationToken).ConfigureAwait(false);
        if (claims.Count == 0)
            return 1.0; // no claims = trivially faithful

        var context = string.Join("\n", sample.SourceChunks);
        var verificationTasks = claims.Select(claim => VerifyClaimAsync(claim, context, cancellationToken));
        var results = await Task.WhenAll(verificationTasks).ConfigureAwait(false);

        return results.Count(r => r) / (double)results.Length;
    }

    private async Task<IReadOnlyList<string>> ExtractClaimsAsync(string answer, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Extract all atomic factual claims from the answer. " +
                "Output a JSON array of strings — one string per claim. No explanation."),
            new(ChatRole.User, answer),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = response.Messages.LastOrDefault()?.Text?.Trim() ?? "[]";
        try
        {
            return JsonSerializer.Deserialize(raw, RagJsonSerializerContext.Default.ListString) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<bool> VerifyClaimAsync(string claim, string context, CancellationToken cancellationToken)
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
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        return answer.StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }
}
