using Microsoft.Extensions.AI;
using ZeroAlloc.Validation;

namespace Rag.NET.Raptor;

/// <summary>Configuration for the RAPTOR ingestion behavior.</summary>
[Validate]
public sealed class RaptorOptions
{
    /// <summary>Toggle RAPTOR tree building on/off. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skip RAPTOR if the document has fewer embedded chunks than this. Default: 5.</summary>
    public int MinChunksForRaptor { get; set; } = 5;

    /// <summary>
    /// UMAP target dimensionality for clustering. Default: 10.
    /// <para>
    /// Must be greater than 0 — enforced by the validation attribute, which
    /// <c>UseRaptor</c> runs through the generated <c>RaptorOptionsValidator</c> at
    /// registration. Zero reduces every embedding to zero dimensions, leaving GMM clustering
    /// nothing to cluster on; a negative value makes <c>Umap</c>'s random-projection setup
    /// throw mid-ingestion (<c>Enumerable.Range</c> rejects a negative count).
    /// </para>
    /// </summary>
    [GreaterThan(0)]
    public int ReducedDimensionality { get; set; } = 10;

    /// <summary>
    /// Cap for GMM cluster count. Null = BIC auto-selects. Default: null.
    /// <para>
    /// When set, must be greater than 1 — enforced by the validation attribute
    /// (<see langword="null"/> passes). <c>RaptorIngestionBehavior.BuildLevelAsync</c> stops
    /// tree building whenever the effective cluster count is not above 1, so a cap of 1 or
    /// lower builds no summary levels at all — RAPTOR silently disabled while
    /// <see cref="Enabled"/> still reads <see langword="true"/>.
    /// </para>
    /// </summary>
    [GreaterThan(1, When = nameof(MaxClustersIsSet))]
    public int? MaxClusters { get; set; }

    /// <summary>Reports whether <see cref="MaxClusters"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="MaxClusters"/> has a value.</returns>
    internal bool MaxClustersIsSet() => MaxClusters is not null;

    /// <summary>
    /// The largest number of chunks a cluster should hold, which bounds how much text one
    /// summarisation prompt can contain. Default: 100.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a floor on the cluster count, not a cap — and it bounds the average cluster
    /// size, not the maximum.</b> <c>SelectClusterCount</c> computes
    /// <c>ceil(count / TargetClusterSize)</c> and never chooses a <c>k</c> below it, which
    /// guarantees at least that many clusters and therefore an <i>average</i> cluster at or under
    /// this size. It does not guarantee every individual cluster is: GMM assignment is free to put
    /// most of a level's chunks into one component and spread the rest thinly, and nothing here
    /// stops that. A hard per-cluster bound would need clusters split after assignment, which this
    /// deliberately does not do — see below. Below the target the option does nothing: the floor is
    /// 1, and BIC picks <c>k</c> exactly as it did before this option existed.
    /// </para>
    /// <para>
    /// <b>Why it exists.</b> Before it, <c>k</c> was capped at 10 per level regardless of the
    /// level's size, and the joined cluster text had no bound at all. On a 17,648-chunk corpus the
    /// smallest possible largest cluster was 1,765 chunks — about 730,000 characters, roughly
    /// 183,000 tokens against a 128,000-token context. The tree could not be built at any <c>k</c>
    /// the cap allowed (#345). The floor materially reduces the expected maximum — on a balanced
    /// split, from ~1,765 chunks down to ~100 at this option's default — even though it cannot
    /// guarantee it. Whether that reduction is enough in practice is a question for measurement on
    /// real corpora: if clusters come out roughly even, the floor is sufficient on its own; if they
    /// come out lopsided, that is evidence a hard split is needed, not an assumption to make ahead
    /// of the data.
    /// </para>
    /// <para>
    /// <b>It counts chunks, not tokens.</b> At the stock <c>ChunkingOptions.MaxChunkSize</c> of 512
    /// characters, 100 chunks is at most ~51,000 characters — comfortably inside a 128,000-token
    /// context, assuming a roughly balanced split. A larger chunk size, a model with a smaller
    /// context, or evidence of unbalanced clustering all want a smaller target.
    /// </para>
    /// <para>
    /// Must be greater than 1 — enforced by the validation attribute. A target of 1 would put every
    /// chunk in its own cluster, which is #333's degenerate shape reached from the other direction
    /// and a level that never reduces.
    /// </para>
    /// </remarks>
    [GreaterThan(1)]
    public int TargetClusterSize { get; set; } = 100;

    /// <summary>
    /// Cap recursion depth. Null = recurse until a level can no longer be usefully split. Default:
    /// null.
    /// <para>
    /// Not literally "until one cluster remains": <c>Min(MaxClusters, count - 1)</c> forces a
    /// strict decrease every level, and the non-reducing-level guard rejects any level whose
    /// cluster count would not shrink the count, so the smallest level that can still be built is
    /// two nodes clustering to one — which is itself rejected (<c>k &lt;= 1</c> stops the tree). The
    /// top level therefore always has at least two nodes; recursion stops one level short of a
    /// single cluster, not at one.
    /// </para>
    /// <para>
    /// When set, must be greater than 0 — enforced by the validation attribute
    /// (<see langword="null"/> passes). <c>RaptorIngestionBehavior</c> gates level building on
    /// <c>level &lt; MaxTreeDepth</c>, so zero (or a negative depth) builds no levels —
    /// RAPTOR silently disabled while <see cref="Enabled"/> still reads <see langword="true"/>.
    /// </para>
    /// </summary>
    [GreaterThan(0, When = nameof(MaxTreeDepthIsSet))]
    public int? MaxTreeDepth { get; set; }

    /// <summary>Reports whether <see cref="MaxTreeDepth"/> is set, so the bound only applies then.</summary>
    /// <returns>Whether <see cref="MaxTreeDepth"/> has a value.</returns>
    internal bool MaxTreeDepthIsSet() => MaxTreeDepth is not null;

    /// <summary>Keep original leaf chunks alongside summaries. Default: true.</summary>
    public bool StoreLeafChunks { get; set; } = true;

    /// <summary>LLM prompt template for cluster summarization. {chunks} is replaced with concatenated text.</summary>
    public string SummaryPrompt { get; set; } = """
        You are a summarization assistant. Below are several related text passages from the same document cluster.
        Write a concise, comprehensive summary that captures all key information.

        Passages:
        {chunks}

        Summary:
        """;

    /// <summary>Optional separate chat client for summaries (e.g. a cheaper model). Null = use DI-registered IChatClient.</summary>
    public IChatClient? SummaryChatClient { get; set; }

    /// <summary>Optional separate embedder for summaries. Null = use DI-registered IEmbeddingGenerator.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? SummaryEmbedder { get; set; }

    /// <summary>What set of chunks the tree is built over. Default: <see cref="RaptorTreeScope.Corpus"/>.</summary>
    /// <remarks>
    /// <b>The default changed in v1.0 and this is a breaking change.</b> It was
    /// <see cref="RaptorTreeScope.PerDocument"/>, which cannot produce a summary spanning two
    /// documents and therefore is not the mechanism the RAPTOR paper describes (#331).
    /// <see cref="RaptorTreeScope.PerDocument"/> remains fully supported.
    /// </remarks>
    public RaptorTreeScope TreeScope { get; set; } = RaptorTreeScope.Corpus;

    /// <summary>
    /// Under <see cref="RaptorTreeScope.Corpus"/>, the fractional growth in stored leaves that
    /// triggers a tree rebuild. Default: 0.10 — rebuild once the corpus is 10% larger than it was
    /// at the last build. Zero or negative rebuilds on every ingest.
    /// <para>
    /// The same shape and default as <c>GraphRagOptions.CommunityDetectionGrowthThreshold</c>, and
    /// for the same reason: clustering the whole corpus once per ingested document is #300's defect,
    /// and this is the debounce that stops it recurring here.
    /// </para>
    /// </summary>
    [InclusiveBetween(0.0, 100.0)]
    [Must(nameof(CorpusGrowthThresholdIsFinite), Message =
        "CorpusGrowthThreshold must be a finite number (not NaN or infinity).")]
    public double CorpusGrowthThreshold { get; set; } = 0.10;

    /// <summary>Reports whether <see cref="CorpusGrowthThreshold"/> is a finite number.</summary>
    /// <param name="value">The <see cref="CorpusGrowthThreshold"/> under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool CorpusGrowthThresholdIsFinite(double value) => double.IsFinite(value);
}
