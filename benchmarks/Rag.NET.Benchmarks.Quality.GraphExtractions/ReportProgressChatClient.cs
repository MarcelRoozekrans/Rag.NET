using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// Prints one line per community report as the report stage makes it, and delegates everything else
/// to the <see cref="CachedGraphRagClient"/> underneath.
/// <para>
/// <b>It exists because the report stage has no other place to say where it is.</b> Extraction
/// reports progress per article, from the loop that runs the articles; community reports are
/// generated inside <c>CommunityDetectionBehavior</c>, one at a time, and that behavior is the
/// library's — the guard runs it too, so it cannot be taught to print. A stage that spends hundreds
/// of sequential round trips in silence is one nobody can tell from a hung one.
/// </para>
/// </summary>
/// <remarks>
/// It passes the messages through untouched, which is the only property that matters here: the
/// cache key is the rendered prompt, and a decorator that rewrote so much as a separator would make
/// the run write entries under keys the guard never computes.
/// </remarks>
public sealed class ReportProgressChatClient : IChatClient
{
    private readonly CachedGraphRagClient _inner;
    private readonly long _planned;
    private long _completed;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The cached client every request is answered by.</param>
    /// <param name="planned">
    /// How many reports the plan said this run would generate, printed alongside the running count
    /// so a line reads as progress rather than as a tally. Reports already on disk are not in it —
    /// they cost nothing and are named as cached when they go past.
    /// </param>
    public ReportProgressChatClient(CachedGraphRagClient inner, long planned)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _planned = planned;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var misses = _inner.Cache.Misses;
        var response = await _inner.GetResponseAsync(messages, options, cancellationToken);

        var generated = _inner.Cache.Misses > misses;
        Console.WriteLine(FormattableString.Invariant(
            $"[report {Interlocked.Increment(ref _completed)}] {(generated ? "generated" : "cached")}, {response.Text.Length} characters (totals: {_inner.Cache.Hits} cached, {_inner.Cache.Misses} of {_planned} generated)"));

        return response;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The inner client is disposed by whoever created it — this decorator borrows it for the
    /// length of one run and owns nothing.
    /// </remarks>
    public void Dispose()
    {
    }
}
