using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;

namespace Rag.NET.GraphRag;

/// <summary>
/// Rebuilds the graph's derived projections — communities, PageRank scores and the community
/// report chunks — on demand, over the whole graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Community detection is a corpus-level operation that was wearing a per-document costume.</b>
/// <see cref="CommunityDetectionBehavior"/> is an <c>IIngestionBehavior</c>, so it ran once per
/// ingested document, each time loading the entire graph, running Leiden and PageRank over it and
/// writing every score back. On a 17,648-document corpus that is 17,648 whole-graph recomputes, and
/// every one but the last was discarded rather than merged — detection is a pure function of the
/// graph and each run overwrites the previous one (#300).
/// </para>
/// <para>
/// Ingestion now debounces on graph growth
/// (<see cref="GraphRagOptions.CommunityDetectionGrowthThreshold"/>), which makes the ingest cheap
/// and leaves the projections up to that fraction stale. This type is the other half of that trade:
/// the way to say "make them current now" — after a bulk load, before measuring, or on a schedule.
/// </para>
/// <para>
/// <b>The benchmark harness already did this by hand.</b> <c>GraphRagSliceIngestion</c> builds a
/// synthetic context under a <c>graphrag://communities</c> document id and invokes the behaviour
/// once, outside per-document ingestion, because the per-document path was unusable at corpus scale.
/// This is that workaround made into a supported operation, using the same document-id convention.
/// </para>
/// <para>
/// <b>Reports are attributed to a document that is not an article.</b> Report chunks need a
/// <see cref="DocumentId"/>, and during ingestion they took whichever document happened to trigger
/// detection — a corpus-level summary filed under one arbitrary article. Here they are filed under
/// <see cref="ReportDocumentId"/>, which makes them addressable: deleting that id removes exactly
/// the reports and nothing else.
/// </para>
/// </remarks>
/// <param name="behavior">The detection implementation, shared with the ingestion path.</param>
/// <param name="vectorStore">Where the report chunks are written.</param>
public sealed class GraphProjectionRebuilder(
    CommunityDetectionBehavior behavior,
    IVectorStore vectorStore)
{
    /// <summary>
    /// The document id community report chunks are stored under.
    /// </summary>
    /// <remarks>
    /// A URI-shaped id rather than a plausible file name, so it cannot collide with a real document
    /// and reads as synthetic wherever it surfaces. The same value the benchmark harness has used.
    /// </remarks>
    public const string ReportDocumentId = "graphrag://communities";

    /// <summary>
    /// Recomputes communities and PageRank over the whole graph and replaces the stored reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs regardless of <see cref="GraphRagOptions.CommunityDetectionGrowthThreshold"/> — the point
    /// of calling this is that the caller wants the projections current — and resets the threshold's
    /// baseline, so ingestion continuing afterwards debounces from this state rather than a stale one.
    /// </para>
    /// <para>
    /// <b>Old report chunks are deleted before the new ones are stored.</b> Communities are not
    /// stable across runs — Leiden may return a different number of them — so without the delete a
    /// shrinking community count would leave the surplus behind as orphans that retrieval could still
    /// return. Deleting by <see cref="ReportDocumentId"/> touches nothing else.
    /// </para>
    /// <para>
    /// Not safe to run concurrently with itself against one store: two rebuilds would interleave a
    /// delete with the other's store. Callers scheduling this should serialise it, which is the
    /// ordinary expectation for a maintenance operation.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many communities the rebuild produced; zero when the graph holds no entities.</returns>
    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(ReportDocumentId),
                FileName = ReportDocumentId,
            },
            GetNextBm25DocId = () => 0,
        };

        var communities = await behavior.DetectNowAsync(ctx, cancellationToken).ConfigureAwait(false);
        if (communities == 0)
        {
            return 0;
        }

        await vectorStore
            .DeleteByDocumentIdAsync(ReportDocumentId, cancellationToken)
            .ConfigureAwait(false);

        await vectorStore
            .StoreAsync(ctx.EmbeddedChunks, cancellationToken)
            .ConfigureAwait(false);

        return communities;
    }
}
