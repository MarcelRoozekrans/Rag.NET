using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The <see cref="IChatClient"/> a community-report run is costed with: every report prompt is
/// looked up in the report directory of <see cref="GraphExtractionCache"/> and counted, and <b>no
/// model is ever called</b>. The sibling of <see cref="GraphExtractionPlanProbe"/>, one stage later.
/// <para>
/// <b>A whole detection pass rather than a count of communities, because the prompt is the key.</b>
/// What a report costs is decided by <c>CommunityDetectionBehavior.BuildReportPrompt</c> — which
/// members fit inside <c>GraphRagOptions.MaxCommunityReportPromptLength</c>, in PageRank order, with
/// which relationships after them — and none of that is knowable from the community list alone.
/// Driving the real behavior with this client reproduces exactly the keys the paying run will
/// compute, which is what makes "how many of these reports are already bought" an answer rather than
/// an estimate.
/// </para>
/// <para>
/// <b>A miss answers with a placeholder, and unlike the extraction probe's empty extraction that
/// answer is inert.</b> Extraction's follow-up prompt embeds the previous response, so what the
/// probe answers there decides which second key it walks to; a report prompt is a function of the
/// graph alone, so nothing downstream reads this text. It is stored as that community's report in a
/// graph store the plan throws away, and it is deliberately not blank, so a run that mistook the
/// probe for the real client would produce visibly wrong reports rather than empty ones.
/// </para>
/// </summary>
/// <remarks>
/// Nothing is written. The probe reads through <see cref="GraphExtractionCache.TryGet"/>, which
/// neither generates nor moves the cache's hit and miss tallies, so a plan cannot leave a mark on
/// the run it is planning.
/// </remarks>
public sealed class GraphReportPlanProbe : IChatClient
{
    /// <summary>The answer to a miss: text no reader could mistake for a generated report.</summary>
    public const string MissPlaceholder = "(no cached community report — this run would generate it)";

    private readonly GraphExtractionCache _cache;
    private long _cached;
    private long _uncached;

    /// <summary>Creates the probe.</summary>
    /// <param name="cache">The report cache every prompt is looked up in, and never written to.</param>
    public GraphReportPlanProbe(GraphExtractionCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
    }

    /// <summary>Gets how many of this run's reports are already stored, and therefore free.</summary>
    public long Cached => Interlocked.Read(ref _cached);

    /// <summary>Gets how many of this run's reports will have to be generated, and paid for.</summary>
    public long Uncached => Interlocked.Read(ref _uncached);

    /// <summary>Gets how many communities the detection pass asked for a report about.</summary>
    /// <remarks>
    /// One request per community, so this is the community count as the paying run will see it —
    /// counted from the same pass that costed it rather than from a second call to Leiden that
    /// could disagree.
    /// </remarks>
    public long Communities => Cached + Uncached;

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
            new ChatResponse(new ChatMessage(ChatRole.Assistant, stored ?? MissPlaceholder)));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The report plan probe does not stream, for the same reason the cached client does " +
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
