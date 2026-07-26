using Pinecone;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using PineconeIndexModel = Pinecone.Index;
// The Pinecone SDK also declares a SparseValues message; the Rag.NET contract type wins.
using SparseVector = Rag.NET.Models.SparseVector;

namespace Rag.NET.Pinecone;

/// <summary>
/// Sparse-capable <see cref="PineconeVectorStore"/>: serves <see cref="ISparseSearchable"/>
/// through Pinecone's native sparse values, stored on the same records as the dense
/// vectors. Record ids are already deterministic (<c>{documentId}:{chunkIndex}</c>), so
/// <see cref="StoreSparseAsync"/> simply re-upserts the full record (dense + sparse +
/// metadata) — idempotent regardless of ordering with <c>StoreAsync</c>.
/// <para>
/// Pinecone only accepts sparse values on indexes with the <b>dotproduct</b> metric
/// (a cosine index accepts the upsert but rejects the query — a silent time bomb), so
/// <c>CreateCollectionAsync</c> creates dotproduct indexes and the first data-plane use
/// fails fast when the configured index has any other metric. Sparse scores are raw
/// dot products of matching term weights (unbounded above — not cosine similarities);
/// <c>MinScore</c> applies on that scale.
/// </para>
/// <para>
/// This is a separate type (registered by <c>UsePinecone</c> with
/// <c>EnableSparseVectors = true</c>) rather than a flag on
/// <see cref="PineconeVectorStore"/> so the pipelines' capability probe
/// (<c>store is ISparseSearchable</c>) is honest — a dense-only Pinecone store never
/// triggers sparse encoding work (the Qdrant type-split precedent).
/// </para>
/// </summary>
public sealed class PineconeSparseVectorStore : PineconeVectorStore, ISparseSearchable
{
    public PineconeSparseVectorStore(PineconeOptions options)
        : base(options)
    {
    }

    /// <summary>Sparse values require dotproduct — see the class remarks.</summary>
    private protected override CreateIndexRequestMetric IndexMetric => CreateIndexRequestMetric.Dotproduct;

    /// <summary>
    /// Fails fast when the existing index cannot host sparse values — a startup failure
    /// naming the fix beats Pinecone's per-query rejection (the real service accepts
    /// sparse upserts into a cosine index and only errors when querying).
    /// </summary>
    private protected override void ValidateIndexModel(PineconeIndexModel model)
    {
        if (model.Metric != IndexModelMetric.Dotproduct)
        {
            throw new InvalidOperationException(
                $"Pinecone index '{IndexName}' uses the '{model.Metric}' metric, but sparse vectors " +
                "require 'dotproduct'. Delete the index and recreate it through this store's " +
                "CreateCollectionAsync (which uses dotproduct while sparse vectors are enabled), " +
                "then re-ingest.");
        }
    }

    /// <inheritdoc />
    public async Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var vectors = new List<Vector>(items.Count);
        foreach (var (chunk, sparse) in items)
        {
            if (sparse.Count == 0)
                continue; // no terms — nothing to attach

            vectors.Add(BuildVector(chunk, ToSparseValues(sparse)));
        }

        if (vectors.Count == 0)
            return;

        await UpsertBatchedAsync(vectors, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (query.Count == 0)
            return [];

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var response = await index.QueryAsync(
            new QueryRequest
            {
                // Pinecone requires a dense vector on every query, even for sparse-only
                // intent. An all-zero dense vector nulls the dense contribution — the
                // documented alpha-weighting scheme with alpha = 0 (valid on dotproduct;
                // cosine is the metric that rejects zero vectors).
                Vector = new float[VectorDimensions],
                SparseVector = ToSparseValues(query),
                TopK = (uint)options.TopK,
                Filter = BuildFilter(options.MetadataFilter),
                IncludeMetadata = true,
                Namespace = Namespace,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return MapMatches(response, options.MinScore);
    }

    private static SparseValues ToSparseValues(SparseVector sparse)
    {
        var span = sparse.Indices.Span;
        var indices = new uint[span.Length];
        for (var i = 0; i < span.Length; i++)
            indices[i] = (uint)span[i];
        return new SparseValues { Indices = indices, Values = sparse.Values };
    }
}
