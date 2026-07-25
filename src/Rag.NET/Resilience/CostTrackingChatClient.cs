using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IChatClient"/> decorator that enforces daily/monthly spend limits before
/// each call and records the call's token usage and cost to the <see cref="ICostLedger"/>
/// afterwards.
/// </summary>
/// <remarks>
/// The gate is pre-call: every call admitted before the limit is reached completes, so
/// the overshoot can be several in-flight calls' worth under concurrency (documented).
/// Token counts come from <see cref="ChatResponse.Usage"/> when the
/// provider reports BOTH input and output counts; otherwise both sides are estimated with
/// the tiktoken cl100k tokenizer. Streaming responses record ONCE after the stream
/// completes, using the last <see cref="UsageContent"/> the provider emitted in the
/// updates when present, else estimating from the accumulated text; a stream abandoned
/// mid-way (cancellation or fault) is deliberately NOT recorded — its true usage is
/// unknown and guessing would corrupt the ledger. Ledger read/write failures degrade to
/// warnings (an unhealthy ledger never blocks or fails calls). The decorator owns neither
/// the inner client nor the ledger, so <see cref="Dispose"/> disposes nothing.
/// </remarks>
public sealed class CostTrackingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly ICostLedger _ledger;
    private readonly CostBudgetOptions _options;
    private readonly ILogger _logger;

    public CostTrackingChatClient(
        IChatClient inner,
        ICostLedger ledger,
        CostBudgetOptions options,
        ILogger<CostTrackingChatClient>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<CostTrackingChatClient>.Instance;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await CostAccounting.EnforceBudgetAsync(_ledger, _options, _logger, cancellationToken).ConfigureAwait(false);

        // Materialise once: the messages are re-enumerated for estimation when the provider
        // does not report usage, and a lazy enumerable must not be walked twice.
        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var response = await _inner.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false);

        await RecordUsageAsync(messageList, response.Text, response.Usage, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await CostAccounting.EnforceBudgetAsync(_ledger, _options, _logger, cancellationToken).ConfigureAwait(false);

        // Materialise once: the messages are re-enumerated for estimation when the provider
        // does not report usage, and a lazy enumerable must not be walked twice.
        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var accumulatedText = new StringBuilder();
        UsageDetails? usage = null;
        await foreach (var update in _inner.GetStreamingResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false))
        {
            usage = ExtractUsage(update) ?? usage; // providers emit usage in a (typically final) update
            accumulatedText.Append(update.Text);
            yield return update;
        }

        // Reached only when the stream completed normally: cancellation or a fault mid-stream
        // skips recording on purpose (partial usage is unknown; see class remarks).
        await RecordUsageAsync(messageList, accumulatedText.ToString(), usage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Last <see cref="UsageContent"/> in the update's contents, if any.</summary>
    private static UsageDetails? ExtractUsage(ChatResponseUpdate update)
    {
        UsageDetails? details = null;
        foreach (var content in update.Contents)
        {
            if (content is UsageContent usage)
            {
                details = usage.Details;
            }
        }

        return details;
    }

    private Task RecordUsageAsync(
        IEnumerable<ChatMessage> messages,
        string responseText,
        UsageDetails? usage,
        CancellationToken cancellationToken)
    {
        long inputTokens;
        long outputTokens;
        if (usage is { InputTokenCount: { } reportedIn, OutputTokenCount: { } reportedOut })
        {
            inputTokens = reportedIn;
            outputTokens = reportedOut;
        }
        else
        {
            // Estimation fallback: BOTH counts must be provider-reported to be trusted —
            // mixing a reported side with an estimated side would skew the ledger silently.
            inputTokens = CostAccounting.CountMessageTokens(messages);
            outputTokens = CostAccounting.CountTokens(responseText);
        }

        var entry = new CostEntry
        {
            Kind = CostKind.Chat,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = (inputTokens / 1_000_000m * _options.InputPricePerMTokens)
                 + (outputTokens / 1_000_000m * _options.OutputPricePerMTokens),
        };
        return CostAccounting.RecordAsync(_ledger, entry, _logger, cancellationToken);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose() { /* inner client and ledger are externally owned */ }
}
