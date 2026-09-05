using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Retrieval with the metadata filter written by a real model, driven through the <b>real
/// pipeline</b> so the filter is applied exactly where the library applies it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It drives <c>AddRagNet</c> rather than rebuilding the chain, and that was a correction.</b>
/// The first draft composed the three steps by hand — generate, search, <c>Where</c> — which looked
/// faithful and was not: <c>SelfQueryBehavior</c> also REWRITES the query, passing the rewrite down
/// through <c>EmbeddingTextOverride</c>, and that property is <c>internal</c> to Rag.NET. A
/// hand-built chain silently embeds the original query, measures half the technique, and reports it
/// under the technique's name. Running the shipped pipeline gets the rewrite, the filter and the
/// ordering without widening the library's internals for a benchmark.
/// </para>
/// <para>
/// <b>Its figure must NOT be read against the tag-filtered cell's 0.67742.</b> That number came from
/// <c>MetadataFilter</c>, which <c>InMemoryVectorStore</c> applies while scoring, so it returns a
/// full page of the requested corpus. Self-query instead sets <c>RetrievalOptions.Filter</c>, which
/// <c>FilterBehavior</c> applies as <c>results.Where(...)</c> AFTER the search — no over-fetch, no
/// backfill. The page shrinks. The gap between the two figures is the cost of that wiring.
/// </para>
/// <para>
/// <b>How the discard is observed at all.</b> The pipeline returns the filtered page and says
/// nothing about what it dropped, so each query is also retrieved with <c>UseSelfQuery</c> off —
/// no model call, no filter, a full page — and the difference in page size is what the filter
/// discarded. That control costs nothing beyond a second dense search.
/// </para>
/// </remarks>
public sealed class SelfQueryAblationRow : AblationRow, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<AttributeInfo> _schema;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly string _expectedCorpus;
    private ServiceProvider? _provider;
    private IRagPipeline? _pipeline;

    /// <summary>Creates the row over a chat client and embedder the caller owns.</summary>
    /// <param name="chatClient">The model, expected to be cache-backed.</param>
    /// <param name="embedder">The embedder the pipeline uses to embed queries.</param>
    /// <param name="schema">The attributes the model may filter on.</param>
    /// <param name="expectedCorpus">The corpus tag these queries are judged against.</param>
    public SelfQueryAblationRow(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        IReadOnlyList<AttributeInfo> schema,
        string expectedCorpus)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCorpus);

        _chatClient = chatClient;
        _embedder = embedder;
        _schema = schema;
        _expectedCorpus = expectedCorpus;
    }

    /// <summary>Gets how many queries this row has retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <summary>Gets how many came back with a page the filter had changed.</summary>
    public int FilteredQueryCount { get; private set; }

    /// <summary>Gets how many came back with a page the filter did not change.</summary>
    /// <remarks>
    /// Either no filter was produced, or one was produced that excluded nothing on that page. Those
    /// are different events and this row cannot separate them from outside the pipeline, so it
    /// reports the count it can defend rather than guessing which happened.
    /// </remarks>
    public int UnchangedPageCount { get; private set; }

    /// <summary>Gets how many queries kept at least one chunk of the corpus under measurement.</summary>
    public int CorrectCorpusCount { get; private set; }

    /// <summary>Gets how many retrieved hits the post-retrieval filter discarded.</summary>
    /// <remarks>
    /// The direct measure of what the shipped wiring costs: every discarded hit is a slot the caller
    /// asked for and did not get, because nothing over-fetches to replace it.
    /// </remarks>
    public int DiscardedHitCount { get; private set; }

    /// <inheritdoc/>
    public override string Name =>
        "+self-query (model-written filter, applied AFTER retrieval as the pipeline applies it)";

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        BeirQuery query,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        InMemoryVectorStore store,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        var pipeline = Pipeline(store);

        var filtered = await pipeline.RetrieveAsync(
            query.Text,
            new RetrievalOptions { TopK = options.TopK, UseSelfQuery = true },
            cancellationToken);

        Assert.True(
            filtered.IsSuccess,
            filtered.IsSuccess ? string.Empty : $"self-query retrieval failed: {filtered.Error}");

        var control = await pipeline.RetrieveAsync(
            query.Text,
            new RetrievalOptions { TopK = options.TopK, UseSelfQuery = false },
            cancellationToken);

        Assert.True(
            control.IsSuccess,
            control.IsSuccess ? string.Empty : $"the control retrieval failed: {control.Error}");

        QueryCount++;

        var discarded = control.Value.Count - filtered.Value.Count;
        if (discarded > 0)
        {
            FilteredQueryCount++;
            DiscardedHitCount += discarded;
        }
        else
        {
            UnchangedPageCount++;
        }

        if (KeptTheRightCorpus(filtered.Value))
            CorrectCorpusCount++;

        return ToChunkHits(filtered.Value);
    }

    /// <summary>
    /// Asserts the model actually filtered, before this row's figure is read as self-query's.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// A run where no page ever changed returns the dense page on every query and scores the dense
    /// figure exactly — a clean-looking number describing a technique that never ran. Same shape as
    /// the SPLADE cell's expansion guard and the reranker's reordering guard, and for the same
    /// reason: a cell that cannot show its mechanism fired is a number without a claim attached.
    /// </remarks>
    public void AssertTheModelActuallyFiltered(string datasetName)
    {
        Assert.True(
            QueryCount > 0,
            $"{datasetName}: the self-query row retrieved for no queries, so there is nothing to judge.");

        Assert.True(
            FilteredQueryCount > 0,
            FormattableString.Invariant(
                $"{datasetName}: none of {QueryCount} queries came back with a changed page, so ") +
            "either no filter was ever produced or none excluded anything. Either way this figure " +
            "is plain dense retrieval under another name.");

        Assert.True(
            CorrectCorpusCount > 0,
            FormattableString.Invariant(
                $"{datasetName}: not one query kept a chunk tagged '{_expectedCorpus}', the corpus ") +
            "these queries are judged against. A filter that excludes everything relevant is worse " +
            "than no filter, and a count of changed pages alone would have reported it as success.");
    }

    /// <inheritdoc/>
    public void Dispose() => _provider?.Dispose();

    /// <summary>Builds the real pipeline once, over the harness's populated store.</summary>
    private IRagPipeline Pipeline(InMemoryVectorStore store)
    {
        if (_pipeline is not null)
            return _pipeline;

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore>(store);
        services.AddSingleton(_embedder);
        services.AddSingleton(_chatClient);
        services.AddRagNet(rag => rag.UseSelfQuery(_schema));

        _provider = services.BuildServiceProvider();
        _pipeline = _provider.GetRequiredService<IRagPipeline>();

        return _pipeline;
    }

    private bool KeptTheRightCorpus(IReadOnlyList<SearchResult> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Chunk.Metadata.TryGetValue(TagFilteredAblationRow.TagKey, out var tag)
                && string.Equals(tag.StringValue, _expectedCorpus, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
