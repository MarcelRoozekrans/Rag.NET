using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The <see cref="IChatClient"/> a run is costed with: every request is looked up in
/// <see cref="GraphExtractionCache"/> and counted, and <b>no model is ever called</b>.
/// <para>
/// <b>Why a whole extraction pass rather than a count of prompts.</b> Only the first call of each
/// chunk has a prompt that can be computed up front; the gleaning pass's prompt embeds the previous
/// extraction as JSON, so its key exists only once the first response is in hand. Driving the real
/// <c>GraphEntityExtractionBehavior</c> with this client reproduces both keys of every chunk
/// exactly as the paying run will compute them — which is what makes "how many of these requests
/// are already bought" an answer rather than an estimate.
/// </para>
/// <para>
/// <b>A miss answers with an empty extraction, and that is exactly right.</b> The real run would
/// generate text here, glean against it, and store both — so the follow-up prompt this probe cannot
/// know is a request that will certainly be paid for either way. Answering an empty extraction
/// keeps the behavior walking to that second call so it is counted, and contributes nothing to any
/// graph, because nothing here builds one that is kept.
/// </para>
/// </summary>
/// <remarks>
/// Nothing is written. The probe reads through <see cref="GraphExtractionCache.TryGet"/>, which
/// neither generates nor moves the cache's hit and miss tallies, so a plan cannot leave a mark on
/// the run it is planning.
/// </remarks>
public sealed class GraphExtractionPlanProbe : IChatClient
{
    /// <summary>The answer to a miss: parseable, and empty of entities and relationships.</summary>
    private const string EmptyExtraction = """{"entities": [], "relationships": []}""";

    private readonly GraphExtractionCache _cache;
    private long _cached;
    private long _uncached;

    /// <summary>Creates the probe.</summary>
    /// <param name="cache">The cache every request is looked up in, and never written to.</param>
    public GraphExtractionPlanProbe(GraphExtractionCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
    }

    /// <summary>Gets how many of this run's requests are already stored, and therefore free.</summary>
    public long Cached => Interlocked.Read(ref _cached);

    /// <summary>Gets how many of this run's requests will have to be generated, and paid for.</summary>
    public long Uncached => Interlocked.Read(ref _uncached);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var stored = _cache.TryGet(GraphExtractionPrompt.Render(new List<ChatMessage>(messages)));
        _ = stored is null
            ? Interlocked.Increment(ref _uncached)
            : Interlocked.Increment(ref _cached);

        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, stored ?? EmptyExtraction)));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The extraction plan probe does not stream, for the same reason the cached client does " +
            "not: nothing in GraphRAG streams, and a streaming path here would be a request the " +
            "plan never counted.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    /// <remarks>Nothing to dispose: there is no model underneath, which is the point.</remarks>
    public void Dispose()
    {
    }
}
