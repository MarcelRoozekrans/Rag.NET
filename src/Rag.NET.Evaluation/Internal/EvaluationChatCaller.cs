using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Internal;

/// <summary>
/// One system-and-user chat call, under a shared concurrency ceiling, billed to the ledger.
/// </summary>
/// <remarks>
/// Lives here rather than in <c>Rag.NET.Evaluation.Ragas</c> so the dataset builder can reach it:
/// the RAGAS package references this one, so the dependency only runs this way. Copying the
/// throttle-and-cost plumbing instead would recreate Phase 3.1's central failure — the JSON-parse
/// defect existed twice precisely because the plumbing had been copied, and the whole structural
/// fix was to give it one home.
/// </remarks>
/// <param name="chatClient">The model being called.</param>
/// <param name="options">Run tuning: the shared concurrency ceiling and the token prices.</param>
/// <param name="costLedger">Optional ledger; when absent, nothing is billed.</param>
internal sealed class EvaluationChatCaller(
    IChatClient chatClient,
    EvaluationCallOptions options,
    ICostLedger? costLedger = null)
{
    private readonly SemaphoreSlim _gate = new(
        options.MaxConcurrentCalls > 0
            ? options.MaxConcurrentCalls
            : throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentCalls must be at least 1."));

    /// <summary>Makes one call under the ceiling and returns the reply's trimmed text.</summary>
    /// <param name="systemPrompt">The instruction that frames the call.</param>
    /// <param name="userPrompt">The material the call is about.</param>
    /// <param name="cancellationToken">Token to cancel the call.</param>
    public async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt),
            };

            var response = await chatClient
                .GetResponseAsync(messages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await RecordCostAsync(response, cancellationToken).ConfigureAwait(false);
            return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecordCostAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        // No usage reported means no honest entry to write. Recording zero tokens would state as
        // fact that the call was free.
        //
        // Both counters must be provider-reported, not just the Usage object. A provider that
        // returns an empty UsageDetails, or fills only one side, would otherwise get an entry
        // claiming zero tokens and zero cost — and a partially-reported call is worse than an
        // empty one, because it understates spend rather than merely stating none. Mixing a
        // reported side with an assumed one skews the ledger silently. Same reasoning as
        // CostTrackingChatClient, and as the embedding path in AnswerRelevanceEvaluator.
        if (costLedger is null ||
            response.Usage is not { InputTokenCount: { } input, OutputTokenCount: { } output })
        {
            return;
        }

        var entry = new CostEntry
        {
            Kind = CostKind.Chat,
            InputTokens = input,
            OutputTokens = output,
            Cost = (input * options.PricePerInputToken) + (output * options.PricePerOutputToken),
        };

        try
        {
            await costLedger.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Billing visibility must never break an evaluation run: the call has already been
            // paid for, and failing it here would lose the result over a bookkeeping error.
        }
    }
}
