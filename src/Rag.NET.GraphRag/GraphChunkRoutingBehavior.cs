using System.Runtime.InteropServices;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.GraphRag;

/// <summary>
/// Moves the graph's own chunks out of the ingestion batch and into
/// <see cref="GraphChunkStore"/>, so they never reach the store holding your documents (#247).
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, rather than three.</b> <see cref="GraphEntityExtractionBehavior"/> adds entity and
/// relationship chunks to the batch and <see cref="CommunityDetectionBehavior"/> adds report chunks;
/// neither needed changing. This runs after both and before storage, takes what is tagged, and
/// writes it elsewhere. A future kind of graph chunk is separated by being tagged, without a fourth
/// edit here.
/// </para>
/// <para>
/// <b>Why not route inside <c>StorageBehavior</c>.</b> That is the obvious single point, and it is in
/// the core package — it would mean core knowing what <c>graph_type</c> means. The separation is a
/// GraphRAG concern and belongs in the GraphRAG package.
/// </para>
/// <para>
/// <b><c>graph_type</c> is the discriminator and it already exists.</b> Every synthetic chunk has
/// carried it since it was written, so no re-tagging, no re-embedding and no schema change is
/// involved — only where the chunk is put.
/// </para>
/// <para>
/// <b>Breaking for an existing index.</b> A store filled before this change still holds the synthetic
/// chunks mixed in with the articles, and nothing here removes them: this routes what is being
/// ingested now. Re-ingest to get a clean split.
/// </para>
/// </remarks>
/// <param name="chunkStore">Where the graph's chunks go.</param>
public sealed class GraphChunkRoutingBehavior(GraphChunkStore chunkStore) : IIngestionBehavior
{
    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx,
        CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(next);

        var graphChunks = new List<EmbeddedChunk>();
        var keep = new List<EmbeddedChunk>(ctx.EmbeddedChunks.Count);

        foreach (ref readonly var chunk in CollectionsMarshal.AsSpan(ctx.EmbeddedChunks))
        {
            if (chunk.Chunk.Metadata.ContainsKey("graph_type"))
            {
                graphChunks.Add(chunk);
            }
            else
            {
                keep.Add(chunk);
            }
        }

        if (graphChunks.Count == 0)
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.graphrag.route");
        activity?.SetTag("graphrag.route.graph_chunks", graphChunks.Count);
        activity?.SetTag("graphrag.route.document_chunks", keep.Count);

        await chunkStore.Store.StoreAsync(graphChunks, ct).ConfigureAwait(false);

        // Rewritten in place: IngestionContext exposes EmbeddedChunks as a List the pipeline shares,
        // so the downstream StorageBehavior must see the reduced batch rather than a copy of it.
        ctx.EmbeddedChunks.Clear();
        ctx.EmbeddedChunks.AddRange(keep);

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
