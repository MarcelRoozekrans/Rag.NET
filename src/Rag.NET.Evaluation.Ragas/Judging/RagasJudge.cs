using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>
/// Owns every LLM interaction the RAGAS metrics need: prompting, parsing, throttling and cost.
/// </summary>
/// <remarks>
/// Exists so the metrics themselves are arithmetic over judgement arrays. Before Phase 3.1 each
/// evaluator carried its own copy of this plumbing, which is how the same JSON-parse defect came
/// to exist twice and the same brittle verdict parsing three times.
/// </remarks>
/// <param name="chatClient">The model asked for judgements.</param>
/// <param name="options">Run tuning: the shared concurrency ceiling and the token prices.</param>
/// <param name="costLedger">Optional ledger; when absent, nothing is billed.</param>
internal sealed class RagasJudge(
    IChatClient chatClient,
    RagasOptions options,
    ICostLedger? costLedger = null)
{
    private readonly SemaphoreSlim _gate = new(
        options.MaxConcurrentCalls > 0
            ? options.MaxConcurrentCalls
            : throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrentCalls must be at least 1."));

    /// <summary>Asks for a yes/no judgement.</summary>
    /// <param name="systemPrompt">The instruction that frames the judgement.</param>
    /// <param name="userPrompt">The material being judged.</param>
    /// <param name="cancellationToken">Token to cancel the call.</param>
    public async Task<Verdict> ClassifyAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        return ParseVerdict(reply);
    }

    /// <summary>Judges many items under the shared concurrency ceiling, preserving input order.</summary>
    /// <param name="systemPrompt">The instruction that frames every judgement.</param>
    /// <param name="items">The items to judge, in the order the result must preserve.</param>
    /// <param name="toUserPrompt">Builds the user prompt for one item.</param>
    /// <param name="cancellationToken">Token to cancel the calls.</param>
    public async Task<IReadOnlyList<Verdict>> ClassifyManyAsync(
        string systemPrompt,
        IReadOnlyList<string> items,
        Func<string, string> toUserPrompt,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<Verdict>[items.Count];
        for (var i = 0; i < items.Count; i++)
            tasks[i] = ClassifyAsync(systemPrompt, toUserPrompt(items[i]), cancellationToken);

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Asks for a JSON array of strings, distinguishing "empty" from "unreadable".</summary>
    /// <param name="systemPrompt">The instruction that asks for the array.</param>
    /// <param name="userPrompt">The material to extract from.</param>
    /// <param name="cancellationToken">Token to cancel the call.</param>
    public async Task<ExtractionResult> ExtractListAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply))
            return ExtractionResult.Failed();

        try
        {
            var items = JsonSerializer.Deserialize(reply, RagJsonSerializerContext.Default.ListString);
            return items is null ? ExtractionResult.Failed() : ExtractionResult.Success(items);
        }
        catch (JsonException)
        {
            return ExtractionResult.Failed();
        }
    }

    /// <summary>
    /// Exact match after trimming whitespace and trailing punctuation. Anything else is
    /// <see cref="Verdict.Unparseable"/> rather than a guess.
    /// </summary>
    private static Verdict ParseVerdict(string reply)
    {
        var trimmed = reply.Trim().TrimEnd('.', '!', ' ');
        if (string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
            return Verdict.Yes;

        return string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            ? Verdict.No
            : Verdict.Unparseable;
    }

    private async Task<string> CompleteAsync(
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
        if (costLedger is null || response.Usage is not { } usage)
            return;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;

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
            // Billing visibility must never break an evaluation run: the judgement has already
            // been paid for, and failing it here would lose the result over a bookkeeping error.
        }
    }
}
