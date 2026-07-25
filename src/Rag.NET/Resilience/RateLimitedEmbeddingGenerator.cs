using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> decorator that acquires one
/// rate-limit permit before each <see cref="GenerateAsync"/> call, waiting (not rejecting)
/// while the budget is exhausted.
/// </summary>
/// <remarks>
/// Permits are per request, not per input value: chunk batching makes the call the natural
/// unit of provider load. The decorator owns neither the inner generator nor the limiter
/// (a limiter may be shared across decorators), so <see cref="Dispose"/> disposes nothing.
/// </remarks>
public sealed class RateLimitedEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    IRateLimiter rateLimiter) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IRateLimiter _rateLimiter =
        rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

    /// <inheritdoc/>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _rateLimiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        return await _inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers for its own type first (so a stacked decorator chain is probeable layer by
    /// layer), then delegates to the inner generator.
    /// </remarks>
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) == true
            ? this
            : _inner.GetService(serviceType!, serviceKey);

    /// <inheritdoc/>
    public void Dispose() { /* inner generator and limiter are externally owned */ }
}
