using Microsoft.Extensions.AI;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;

namespace Rag.NET.GraphRag;

/// <summary>
/// Searches <see cref="GraphChunkStore"/> for one kind of graph chunk.
/// </summary>
/// <remarks>
/// <para>
/// Since #247 the graph's chunks live in their own store, so the behaviours that need them ask for
/// them directly instead of sifting them out of the caller's results. That is the point of the
/// separation: a search defined over community reports asks the store that holds community reports.
/// </para>
/// <para>
/// <b>The query vector is resolved the same way the core search behaviours resolve it</b> —
/// <c>EmbeddingOverride</c> first, then <c>EmbeddingTextOverride</c>, then the raw query. Without
/// that, a HyDE run would search the document store with a hypothetical-document vector and the
/// graph store with the literal query, and the two halves of one retrieval would disagree about what
/// was being asked. <c>QueryVectorResolver</c> does exactly this inside the core package and is
/// internal to it, so the contract is re-stated here rather than shared.
/// </para>
/// </remarks>
internal static class GraphChunkSearch
{
    public const string GraphTypeKey = "graph_type";

    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(
        GraphChunkStore chunkStore,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        RetrievalContext ctx,
        string graphType,
        int topK,
        CancellationToken ct)
    {
        if (topK <= 0)
        {
            return [];
        }

        var vector = await ResolveQueryVectorAsync(ctx, embedder, ct).ConfigureAwait(false);

        // The caller's own MetadataFilter is preserved and the kind is added, so a caller who scoped
        // retrieval to one tenant or source keeps that scope when the graph store is consulted.
        var filter = ctx.Options.MetadataFilter is not null
            ? new Dictionary<string, MetadataValue>(ctx.Options.MetadataFilter, StringComparer.Ordinal)
            : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        filter[GraphTypeKey] = graphType;

        return await chunkStore.Store.SearchAsync(
            vector,
            new SearchOptions { TopK = topK, MetadataFilter = filter },
            ct).ConfigureAwait(false);
    }

    private static async Task<ReadOnlyMemory<float>> ResolveQueryVectorAsync(
        RetrievalContext ctx,
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        CancellationToken ct)
    {
        // Empty means absent — the same contract TextChunk.Embedding uses.
        if (ctx.Options.EmbeddingOverride is { IsEmpty: false } supplied)
        {
            return supplied;
        }

        var text = ctx.Options.EmbeddingTextOverride ?? ctx.Query;
        var embeddings = await embedder.GenerateAsync([text], cancellationToken: ct).ConfigureAwait(false);
        return embeddings[0].Vector;
    }
}
