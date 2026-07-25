using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;

namespace Rag.NET.Resilience;

/// <summary>
/// An <see cref="IChatClient"/> decorator that acquires one rate-limit permit before each
/// call, waiting (not rejecting) while the budget is exhausted.
/// </summary>
/// <remarks>
/// A streaming call acquires a single permit before iteration of the underlying stream
/// begins — a stream is one request, so individual updates are not permit-counted.
/// The decorator owns neither the inner client nor the limiter (a limiter may be shared
/// across decorators), so <see cref="Dispose"/> disposes nothing.
/// </remarks>
public sealed class RateLimitedChatClient(IChatClient inner, IRateLimiter rateLimiter) : IChatClient
{
    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IRateLimiter _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _rateLimiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        return await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // One permit for the whole stream, acquired before the inner stream is started.
        await _rateLimiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);

        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers for its own type first (so a stacked decorator chain is probeable layer by
    /// layer), then delegates to the inner client.
    /// </remarks>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose() { /* inner client and limiter are externally owned */ }
}
