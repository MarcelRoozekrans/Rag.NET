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
/// Retrieval through the shipped <c>DeepResearchRetriever</c> — a real model judges whether the
/// retrieved context answers the query and, when it says no, writes sub-queries that are retrieved
/// and folded into the page.
/// </summary>
/// <remarks>
/// <para>
/// <b>It drives <c>AddRagNet</c> rather than rebuilding the loop</b>, for the reason
/// <see cref="SelfQueryAblationRow"/> records: a hand-built chain measures whatever the benchmark
/// author reimplemented, under the shipped feature's name. Here the risk is sharper than usual —
/// see the short-circuit below.
/// </para>
/// <para>
/// <b>The control is a SECOND PIPELINE, not a flag.</b> Deep research has no
/// <c>RetrievalOptions</c> switch; <c>UseDeepResearch</c> decorates the retriever at build time.
/// So this row builds two providers over the same store and embedder, differing only in that call.
/// Self-query could toggle <c>UseSelfQuery</c> per request and this cannot.
/// </para>
/// <para>
/// <b>THE SHORT-CIRCUIT IS WHY THE GUARD EXISTS.</b> <c>DeepResearchRetriever</c> fails open: an
/// unreadable sufficiency reply is treated as "sufficient", logged at warning, and the loop stops.
/// That is a defensible default and a measurement hazard, because a run where every reply failed
/// to parse returns the inner page on every query and scores <b>exactly the dense figure</b> — a
/// clean number describing a feature that never ran. <see cref="AssertTheLoopActuallyRan"/> refuses
/// that run. It is the same shape as self-query's filter guard and SPLADE's expansion guard.
/// </para>
/// <para>
/// <b>What this row's page is, precisely.</b> Not <c>TopK</c> chunks: the union of the inner page
/// and every sub-query's page, deduplicated and sorted by score, with no truncation — see
/// issue #475. Two consequences the figure carries and cannot separate: the page is larger than the
/// control's, and its ordering mixes scores taken against different query vectors, so a chunk
/// scoring well against a sub-query can outrank one scoring well against the question asked. This
/// row measures deep research <b>as it ships</b>, which is the entry's question.
/// </para>
/// </remarks>
public sealed class DeepResearchAblationRow : AblationRow, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly DeepResearchOptions _options;

    private ServiceProvider? _deepProvider;
    private ServiceProvider? _controlProvider;
    private IRagPipeline? _deep;
    private IRagPipeline? _control;

    /// <summary>Creates the row over a chat client and embedder the caller owns.</summary>
    /// <param name="chatClient">The model, expected to be cache-backed.</param>
    /// <param name="embedder">The embedder both pipelines use to embed queries and sub-queries.</param>
    /// <param name="options">
    /// The shipped defaults unless a caller says otherwise. <see cref="DeepResearchOptions.MaxDepth"/>
    /// bounds the model calls per query, so it also bounds the cell's cost.
    /// </param>
    public DeepResearchAblationRow(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        DeepResearchOptions? options = null)
    {
        _chatClient = chatClient;
        _embedder = embedder;
        _options = options ?? new DeepResearchOptions();
    }

    /// <summary>Queries this row retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <summary>Queries where the loop expanded the page beyond the control's.</summary>
    /// <remarks>
    /// The observable trace of "the model said insufficient and sub-queries were retrieved". Zero
    /// across a whole run means every reply short-circuited and the figure is the control's.
    /// </remarks>
    public int ExpandedQueryCount { get; private set; }

    /// <summary>Chunks added across every expansion, summed.</summary>
    public int AddedChunkCount { get; private set; }

    /// <summary>Largest page this row returned, against <see cref="RequestedTopK"/>.</summary>
    /// <remarks>Evidence for issue #475, recorded rather than asserted away.</remarks>
    public int LargestPage { get; private set; }

    /// <summary>
    /// The <c>TopK</c> the harness asked for, captured rather than assumed.
    /// </summary>
    /// <remarks>
    /// The harness computes its search depth from the metric cutoff and the corpus's units-per-
    /// document, so there is no constant to quote — and a report that quoted the wrong one would
    /// understate or overstate issue #475 by whatever the difference happened to be.
    /// </remarks>
    public int RequestedTopK { get; private set; }

    /// <inheritdoc/>
    public override string Name =>
        "+deep research (model-judged sufficiency, sub-queries folded in, page NOT capped to TopK)";

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

        RequestedTopK = options.TopK;
        var retrieval = new RetrievalOptions { TopK = options.TopK };

        var deep = await Deep(store).RetrieveAsync(query.Text, retrieval, cancellationToken);
        Assert.True(
            deep.IsSuccess,
            deep.IsSuccess ? string.Empty : $"deep-research retrieval failed: {deep.Error}");

        var control = await Control(store).RetrieveAsync(query.Text, retrieval, cancellationToken);
        Assert.True(
            control.IsSuccess,
            control.IsSuccess ? string.Empty : $"the control retrieval failed: {control.Error}");

        QueryCount++;

        var added = deep.Value.Count - control.Value.Count;
        if (added > 0)
        {
            ExpandedQueryCount++;
            AddedChunkCount += added;
        }

        if (deep.Value.Count > LargestPage)
            LargestPage = deep.Value.Count;

        return ToChunkHits(deep.Value);
    }

    /// <summary>
    /// Asserts the sufficiency loop actually ran, before this row's figure is read as deep
    /// research's.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// <c>DeepResearchRetriever</c> treats an unreadable reply as sufficient and logs a warning.
    /// A benchmark reads no logs. Without this, a run where the model's JSON never parsed reports
    /// the dense figure under this row's name and looks like a clean null result — the most
    /// expensive kind of wrong answer this phase produces.
    /// </remarks>
    public void AssertTheLoopActuallyRan(string datasetName)
    {
        Assert.True(
            QueryCount > 0,
            $"{datasetName}: the deep-research row retrieved for no queries, so there is nothing to judge.");

        Assert.True(
            ExpandedQueryCount > 0,
            FormattableString.Invariant(
                $"{datasetName}: none of {QueryCount} queries expanded past the control's page, ") +
            "so either the model called every context sufficient or every sufficiency reply was " +
            "unreadable and short-circuited. DeepResearchRetriever cannot tell those apart from " +
            "outside and neither can this figure, which is plain dense retrieval under another name.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _deepProvider?.Dispose();
        _controlProvider?.Dispose();
    }

    private IRagPipeline Deep(InMemoryVectorStore store) =>
        _deep ??= Build(store, deepResearch: true, out _deepProvider);

    private IRagPipeline Control(InMemoryVectorStore store) =>
        _control ??= Build(store, deepResearch: false, out _controlProvider);

    /// <summary>Builds one real pipeline over the harness's populated store.</summary>
    private IRagPipeline Build(
        InMemoryVectorStore store, bool deepResearch, out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore>(store);
        services.AddSingleton(_embedder);
        services.AddSingleton(_chatClient);

        services.AddRagNet(rag =>
        {
            if (deepResearch)
                rag.UseDeepResearch(_options);
        });

        provider = services.BuildServiceProvider();

        // The cell is meaningless if the decorator is not in the chain, and the failure is silent:
        // an undecorated pipeline returns the dense page, makes no model call, and reproduces the
        // control's figure exactly. Checked here, where it is one line, rather than inferred at the
        // end of a 300-query run from the fact that nothing changed.
        var retriever = provider.GetRequiredService<IRetriever>();
        Assert.True(
            deepResearch == retriever is Rag.NET.Retrieval.DeepResearchRetriever,
            deepResearch
                ? $"UseDeepResearch was configured and IRetriever resolved to {retriever.GetType().Name}. "
                  + "The cell would have measured plain dense retrieval under deep research's name."
                : $"the control pipeline resolved {retriever.GetType().Name}, which decorates "
                  + "retrieval; it would not be a control.");

        return provider.GetRequiredService<IRagPipeline>();
    }
}
