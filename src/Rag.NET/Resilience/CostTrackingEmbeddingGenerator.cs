using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> decorator that enforces
/// daily/monthly spend limits before each call and records estimated usage to the
/// <see cref="ICostLedger"/> afterwards.
/// </summary>
/// <remarks>
/// Embedding providers rarely report usage, so input tokens are always estimated with the
/// tiktoken cl100k tokenizer over the input values; output tokens are 0 (embeddings have
/// no completion side). The gate is pre-call: every call admitted before the limit is
/// reached completes, so the overshoot can be several in-flight calls' worth under
/// concurrency — parallel ingestion routinely has N embedding batches in flight.
/// Ledger read/write failures degrade to warnings — they never
/// block or fail calls. The decorator owns neither the inner generator nor the ledger, so
/// <see cref="Dispose"/> disposes nothing.
/// </remarks>
public sealed class CostTrackingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly ICostLedger _ledger;
    private readonly CostBudgetOptions _options;
    private readonly ILogger _logger;

    public CostTrackingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        ICostLedger ledger,
        CostBudgetOptions options,
        ILogger<CostTrackingEmbeddingGenerator>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<CostTrackingEmbeddingGenerator>.Instance;
    }

    /// <inheritdoc/>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await CostAccounting.EnforceBudgetAsync(_ledger, _options, _logger, cancellationToken).ConfigureAwait(false);

        // Materialise once: the values are enumerated again below for token estimation.
        var valueList = values as IReadOnlyList<string> ?? [.. values];
        var result = await _inner.GenerateAsync(valueList, options, cancellationToken).ConfigureAwait(false);

        long inputTokens = 0;
        foreach (var value in valueList)
        {
            inputTokens += CostAccounting.CountTokens(value);
        }

        var entry = new CostEntry
        {
            Kind = CostKind.Embedding,
            InputTokens = inputTokens,
            OutputTokens = 0,
            Cost = inputTokens / 1_000_000m * _options.EmbeddingPricePerMTokens,
        };
        await CostAccounting.RecordAsync(_ledger, entry, _logger, cancellationToken).ConfigureAwait(false);
        return result;
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
    public void Dispose() { /* inner generator and ledger are externally owned */ }
}
