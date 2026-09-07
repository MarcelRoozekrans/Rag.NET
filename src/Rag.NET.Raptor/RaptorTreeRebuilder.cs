using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;

namespace Rag.NET.Raptor;

/// <summary>Rebuilds the corpus-level RAPTOR tree on demand, over every stored leaf.</summary>
/// <remarks>
/// <para>
/// Ingestion debounces tree building on corpus growth
/// (<see cref="RaptorOptions.CorpusGrowthThreshold"/>), which keeps the ingest cheap and leaves the
/// tree up to that fraction stale. This type is the other half of that trade: the way to say "make
/// it current now" — after a bulk load, before measuring, or on a schedule.
/// </para>
/// <para>
/// <b>The old tree is deleted before the new one is stored.</b> Clustering is not stable across
/// runs and may return fewer summaries than last time, so without the delete the surplus would
/// remain as orphans that retrieval could still return. Deleting
/// <see cref="RaptorCorpusDocumentId.Value"/> touches nothing else.
/// </para>
/// <para>
/// Not safe to run concurrently with itself against one store: two rebuilds would interleave a
/// delete with the other's store. Callers scheduling this should serialise it.
/// </para>
/// </remarks>
/// <param name="behavior">The tree-building implementation, shared with the ingestion path.</param>
/// <param name="vectorStore">Where the summary chunks are written.</param>
public sealed class RaptorTreeRebuilder(
    RaptorIngestionBehavior behavior, IVectorStore vectorStore, IBm25Index bm25Index)
{
    /// <summary>Rebuilds the tree over every stored leaf and replaces the stored summaries.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many summaries the rebuild produced; zero when the corpus holds fewer than two leaves.</returns>
    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId(RaptorCorpusDocumentId.Value),
                FileName = RaptorCorpusDocumentId.Value,
            },
        };

        var count = await behavior.BuildCorpusTreeNowAsync(ctx, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return 0;
        }

        await vectorStore
            .DeleteByDocumentIdAsync(RaptorCorpusDocumentId.Value, cancellationToken)
            .ConfigureAwait(false);

        await vectorStore
            .StoreAsync(ctx.EmbeddedChunks, cancellationToken)
            .ConfigureAwait(false);

        // BM25 too, which this used to skip entirely (#487). Without it a rebuild left the two
        // stores disagreeing: the vector store held the new tree while BM25 held whatever the
        // ingest path last wrote, so the rebuilt summaries were invisible to keyword and hybrid
        // search and the stale ones were still returned. Remove-then-add for the same reason the
        // vector store is deleted first -- clustering is not stable across runs, so a shorter tree
        // must not leave the surplus behind.
        bm25Index.Remove(RaptorCorpusDocumentId.Value);
        foreach (ref readonly var embedded in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ctx.EmbeddedChunks))
        {
            bm25Index.Add(embedded.Chunk);
        }

        return count;
    }
}
