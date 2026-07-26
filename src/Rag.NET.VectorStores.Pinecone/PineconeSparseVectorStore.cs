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
/// vectors. Record ids are already deterministic (<c>{documentId}:{chunkIndex}</c>) and
/// <see cref="StoreSparseAsync"/> upserts the FULL record (dense + sparse + metadata), so
/// it needs no prior <see cref="PineconeVectorStore.StoreAsync"/> call for the chunk.
/// <para>
/// ORDERING CONTRACT: a Pinecone upsert replaces the entire record, and
/// <c>StoreAsync</c> writes records without sparse values — so calling <c>StoreAsync</c>
/// AFTER <c>StoreSparseAsync</c> for the same chunk silently drops its sparse vector.
/// Always store dense first, sparse second (in-repo ingestion does: <c>StorageBehavior</c>
/// and <c>RegenerateSparseAsync</c> both order it that way). Re-ingesting a chunk means
/// re-running both steps.
/// </para>
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
    /// <summary>
    /// The all-zero dense vector every sparse query carries, cached per resolved
    /// dimension (1536 floats = ~6 KB — not worth reallocating per query). Rebuilt only
    /// if the index dimension resolves to something else.
    /// </summary>
    private float[] _zeroDenseVector = [];

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
                // cosine is the metric that rejects zero vectors). Its length comes from
                // the LIVE index (resolved by the GetIndexAsync describe above), not the
                // configured option — a disagreeing option would fail every query.
                Vector = ZeroDenseVector(),
                SparseVector = ToSparseValues(query),
                TopK = (uint)options.TopK,
                Filter = BuildFilter(options.MetadataFilter),
                IncludeMetadata = true,
                Namespace = Namespace,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return MapMatches(response, options.MinScore);
    }

    /// <summary>
    /// Returns the cached all-zero dense vector for the current resolved dimension,
    /// reallocating only when the dimension differs (i.e. once, right after the first
    /// describe resolves the live index). Publishing a fully built array by reference is
    /// safe under concurrent queries: readers either see the previous correct array or
    /// the new one, never a partially filled buffer.
    /// </summary>
    private float[] ZeroDenseVector()
    {
        var dimensions = VectorDimensions;
        var cached = Volatile.Read(ref _zeroDenseVector);
        if (cached.Length == dimensions)
            return cached;

        var zeroes = new float[dimensions];
        Volatile.Write(ref _zeroDenseVector, zeroes);
        return zeroes;
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
