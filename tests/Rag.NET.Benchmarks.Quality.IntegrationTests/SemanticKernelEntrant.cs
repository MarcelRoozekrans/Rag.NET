using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The Phase 3.14 Semantic Kernel entrant: index each BEIR corpus into Semantic Kernel's InMemory
/// vector store, retrieve for each judged query through its <c>SearchAsync</c>, and hand the
/// rankings to <see cref="TrecRunFile"/> — Semantic Kernel <b>at its own defaults</b>, with the
/// one matched element every entrant gets, the pinned <c>all-MiniLM-L6-v2</c> embedder.
/// <para>
/// <b>Versions measured</b> (pinned in this project's <c>.csproj</c>, read in
/// <c>docs/reference/library-comparison-defaults.md</c> before this entrant was written):
/// <c>Microsoft.SemanticKernel</c> <b>1.78.0</b>;
/// <c>Microsoft.SemanticKernel.Connectors.InMemory</c> <b>1.74.0-preview</b> — no stable release
/// of the connector exists; <c>Microsoft.Extensions.VectorData.Abstractions</c> <b>10.1.0</b>,
/// the version SK 1.78.0 itself pins.
/// </para>
/// <para>
/// <b>One record per document is Semantic Kernel's default, not a harness choice.</b> SK has no
/// ingestion pipeline and no default chunker — its one splitting utility, <c>TextChunker</c>, is
/// <c>[Experimental("SKEXP0050")]</c> and requires the caller to pick a size — so a user who
/// upserts documents into a vector store gets exactly one record per document, and that is what
/// this row measures. Distance is likewise the connector's own default: a vector property with no
/// declared distance function scores with <b>cosine similarity, descending</b>
/// (<c>InMemoryCollectionSearchMapping.CompareVectors</c>, <c>dotnet-1.74.0</c>).
/// </para>
/// <para>
/// <b>What is protocol rather than Semantic Kernel:</b> retrieval depth — SK's
/// <c>SearchAsync</c> has no default <c>top</c> at all, so every entrant retrieves the metric's
/// cutoff (<see cref="BeirHarness.Cutoff"/>), over-shot by one where the dataset's protocol drops
/// the query's own document (<see cref="RetrievalDepth"/>); and the self-exclusion itself, applied
/// on the writer's side of the run-file boundary (<see cref="TopDocuments"/>) exactly as the
/// control row applies it, because a published run file must already hold the post-exclusion
/// ranking. <b>Semantic Kernel's returned order is otherwise untouched</b> — no re-sort, no
/// tie-break of ours — which is why the cut lives here and not in <see cref="DocumentRanking"/>.
/// </para>
/// </summary>
public static class SemanticKernelEntrant
{
    /// <summary>all-MiniLM-L6-v2's output width, declared on the record's vector property.</summary>
    public const int EmbeddingDimension = 384;

    /// <summary>
    /// Documents per <c>UpsertAsync</c> call — the same working-set bound as
    /// <c>BeirHarness.SlabSize</c>, and only a bound: the pinned generator pools excluding padding,
    /// so no batch size can change a document's vector.
    /// </summary>
    private const int SlabSize = 512;

    /// <summary>
    /// One corpus document as Semantic Kernel stores it: the BEIR document id as the key and the
    /// retrieval text as the vector property. The property is a <see cref="string"/> so that
    /// <b>Semantic Kernel's own embedding path</b> — the collection's registered
    /// <c>EmbeddingGenerator</c> — embeds it on upsert; handing it pre-computed vectors would
    /// bypass the very wiring the row exists to measure. No distance function is declared, so the
    /// connector's default (cosine) applies.
    /// </summary>
    public sealed class CorpusRecord
    {
        /// <summary>The BEIR document id — what qrels reference and the run file carries.</summary>
        [VectorStoreKey]
        public string Id { get; set; } = string.Empty;

        /// <summary>The document's retrieval text, embedded by the collection's generator.</summary>
        [VectorStoreVector(EmbeddingDimension)]
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// How deep every query retrieves: the metric's cutoff, plus one where the dataset's protocol
    /// will drop the query's own document — the worst case being that document inside the top
    /// <see cref="BeirHarness.Cutoff"/>, after which the ranking must still hold a full cutoff.
    /// A protocol parameter, not a Semantic Kernel default: <c>SearchAsync</c>'s <c>top</c> is a
    /// required argument with no default anywhere in the stable API.
    /// </summary>
    /// <param name="excludesSelfRetrievedDocument">
    /// <see cref="BeirDatasetDescriptor.ExcludesSelfRetrievedDocument"/>.
    /// </param>
    /// <returns>What to pass as <c>top</c>.</returns>
    public static int RetrievalDepth(bool excludesSelfRetrievedDocument) =>
        BeirHarness.Cutoff + (excludesSelfRetrievedDocument ? 1 : 0);

    /// <summary>
    /// Cuts one query's hits, in the exact order Semantic Kernel returned them, to the ranking the
    /// run file carries: the protocol-excluded document dropped, then the first
    /// <paramref name="cutoff"/> of what remains. Nothing is re-scored and nothing is re-sorted —
    /// the library's own ordering, ties included, is the entrant's ranking.
    /// </summary>
    /// <param name="hitsInSearchOrder">The hits as returned, best first.</param>
    /// <param name="cutoff">How many documents the ranking keeps.</param>
    /// <param name="excludedDocumentId">
    /// The query's own document where the dataset's protocol excludes it, or <see langword="null"/>.
    /// </param>
    /// <returns>The ranking, at most <paramref name="cutoff"/> long.</returns>
    public static IReadOnlyList<ScoredDocument> TopDocuments(
        IReadOnlyList<ScoredDocument> hitsInSearchOrder, int cutoff, string? excludedDocumentId)
    {
        ArgumentNullException.ThrowIfNull(hitsInSearchOrder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cutoff);

        var top = new List<ScoredDocument>(cutoff);
        for (var i = 0; i < hitsInSearchOrder.Count && top.Count < cutoff; i++)
        {
            var hit = hitsInSearchOrder[i];
            if (string.Equals(hit.DocumentId, excludedDocumentId, StringComparison.Ordinal))
            {
                continue;
            }

            top.Add(hit);
        }

        return top;
    }

    /// <summary>
    /// Creates the collection the entrant indexes into, with the pinned embedder registered where
    /// Semantic Kernel's own wiring takes it: <c>VectorStoreCollectionOptions.EmbeddingGenerator</c>.
    /// The caller disposes it.
    /// </summary>
    /// <param name="embeddingGenerator">
    /// The pinned embedder, behind <see cref="RecordingEmbeddingGenerator"/> so the run can prove
    /// what was embedded.
    /// </param>
    /// <returns>An empty named collection; <see cref="IndexAsync"/> creates it in the store.</returns>
    public static InMemoryCollection<string, CorpusRecord> CreateCollection(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) =>
        new("beir", new InMemoryCollectionOptions { EmbeddingGenerator = embeddingGenerator });

    /// <summary>
    /// Indexes the corpus: one record per document, embedded by Semantic Kernel's upsert path
    /// through the collection's registered generator. The measured run prefetches every vector
    /// into the embedding cache's memory first, so the timed span around this call measures SK
    /// building its store from vectors it already has — embedding and its disk I/O excluded by
    /// construction, which is why that figure is not "the cost of indexing".
    /// </summary>
    /// <param name="collection">The collection from <see cref="CreateCollection"/>.</param>
    /// <param name="documents">The corpus.</param>
    /// <param name="cancellationToken">Cancels the indexing.</param>
    public static async Task IndexAsync(
        InMemoryCollection<string, CorpusRecord> collection,
        IReadOnlyList<BeirDocument> documents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(documents);

        await collection.EnsureCollectionExistsAsync(cancellationToken);
        for (var start = 0; start < documents.Count; start += SlabSize)
        {
            var end = Math.Min(start + SlabSize, documents.Count);
            var records = new CorpusRecord[end - start];
            for (var i = start; i < end; i++)
            {
                records[i - start] = new CorpusRecord
                {
                    Id = documents[i].Id,
                    Text = documents[i].RetrievalText,
                };
            }

            await collection.UpsertAsync(records, cancellationToken);
        }
    }

    /// <summary>
    /// Retrieves for every query through Semantic Kernel's <c>SearchAsync</c> — which embeds the
    /// query text through the collection's registered generator — and cuts each result to the run
    /// file's ranking with <see cref="TopDocuments"/>.
    /// <para>
    /// Each query's latency spans <b>the library's retrieval only</b>: the <c>SearchAsync</c>
    /// enumeration, hit collection included, exactly the operation the Python harness brackets as
    /// its entrants' <c>retrieve</c> call. <see cref="TopDocuments"/> stays outside the span —
    /// the cut is harness protocol, deliberately identical across entrants, and timing it would
    /// smear the same constant into every entrant's latency column. The query embedding inside
    /// <c>SearchAsync</c> resolves from the prefetched in-memory cache, so the span holds SK's
    /// search with no disk read to inherit the OS page cache's state.
    /// </para>
    /// </summary>
    /// <param name="collection">The indexed collection.</param>
    /// <param name="queries">The judged queries.</param>
    /// <param name="excludesSelfRetrievedDocument">
    /// Whether the dataset's protocol drops the document sharing the query's id.
    /// </param>
    /// <param name="cancellationToken">Cancels the retrieval.</param>
    /// <returns>
    /// Rankings by query id, best first — the shape <see cref="TrecRunFile.Write"/> takes — with
    /// one raw latency per query in milliseconds, the shape <see cref="EntrantTimings"/> carries.
    /// </returns>
    public static async Task<(IReadOnlyDictionary<string, IReadOnlyList<ScoredDocument>> Runs,
        IReadOnlyDictionary<string, double> QueryLatenciesMilliseconds)> RetrieveAsync(
        InMemoryCollection<string, CorpusRecord> collection,
        IReadOnlyList<BeirQuery> queries,
        bool excludesSelfRetrievedDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(queries);

        var depth = RetrievalDepth(excludesSelfRetrievedDocument);
        var runs = new Dictionary<string, IReadOnlyList<ScoredDocument>>(
            queries.Count, StringComparer.Ordinal);
        var latencies = new Dictionary<string, double>(queries.Count, StringComparer.Ordinal);
        for (var i = 0; i < queries.Count; i++)
        {
            var query = queries[i];
            var hits = new List<ScoredDocument>(depth);
            var startedAt = Stopwatch.GetTimestamp();
            await foreach (var result in collection.SearchAsync(
                query.Text, depth, options: null, cancellationToken))
            {
                hits.Add(new ScoredDocument(result.Record.Id, RequireScore(result, query.Id)));
            }

            latencies[query.Id] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            runs[query.Id] = TopDocuments(
                hits, BeirHarness.Cutoff, excludesSelfRetrievedDocument ? query.Id : null);
        }

        return (runs, latencies);
    }

    /// <summary>
    /// The result's score, which the InMemory connector always sets for a cosine search — absent
    /// would mean the ranking's order can no longer be validated against its scores, so it throws
    /// rather than substituting one.
    /// </summary>
    private static double RequireScore(
        VectorSearchResult<CorpusRecord> result, string queryId) =>
        result.Score ?? throw new InvalidOperationException(
            $"Semantic Kernel returned document '{result.Record.Id}' for query '{queryId}' " +
            "without a score. The run file's non-increasing-score invariant cannot be checked " +
            "over a ranking with a hole in it.");
}
