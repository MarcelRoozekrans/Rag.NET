using Microsoft.Extensions.AI;
using Polly;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> decorator that executes every
/// <see cref="GenerateAsync"/> call through the <c>"rag-net"</c> Polly
/// <see cref="ResiliencePipeline"/> configured by <c>RagBuilder.ConfigureResilience</c>.
/// </summary>
/// <remarks>
/// The input sequence is materialised once before the pipeline runs, so a retried attempt
/// re-sends the same values instead of re-enumerating a one-shot sequence. Cancellation is
/// never retried by the default policy (its retry predicate excludes
/// <see cref="OperationCanceledException"/>) and the caller's token flows into every attempt.
/// The decorator owns neither the inner generator nor the pipeline, so
/// <see cref="Dispose"/> disposes nothing.
/// </remarks>
public sealed class ResilientEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    ResiliencePipeline pipeline) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ResiliencePipeline _pipeline =
        pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <inheritdoc/>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        // Materialise once: a retried attempt must not re-enumerate a one-shot sequence.
        var valueList = values as IReadOnlyList<string> ?? [.. values];

        return await _pipeline.ExecuteAsync(
            static async (state, ct) =>
                await state.Inner.GenerateAsync(state.Values, state.Options, ct).ConfigureAwait(false),
            (Inner: _inner, Values: valueList, Options: options),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers for its own type first (so a stacked decorator chain is probeable layer by
    /// layer), then delegates to the inner generator.
    /// </remarks>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose() { /* inner generator and pipeline are externally owned */ }
}
