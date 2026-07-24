using System.Security.Cryptography;
using System.Text;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
// The Qdrant client also declares a Grpc SparseVector message; the Rag.NET contract type wins.
using SparseVector = Rag.NET.Models.SparseVector;

namespace Rag.NET.Qdrant;

/// <summary>
/// Sparse-capable <see cref="QdrantVectorStore"/>: serves <see cref="ISparseSearchable"/>
/// through a named sparse vector ("splade") stored on the same points as the dense vectors.
/// Point ids are deterministic (derived from <c>DocumentId</c>/<c>ChunkIndex</c>, making
/// <see cref="QdrantVectorStore.StoreAsync"/> idempotent per chunk) so
/// <see cref="StoreSparseAsync"/> can attach sparse vectors to points upserted by
/// <c>StoreAsync</c> — call <c>StoreAsync</c> first, as ingestion does.
/// <para>
/// This is a separate type (registered by <c>UseQdrant(..., enableSparseVectors: true)</c>)
/// rather than a flag on <see cref="QdrantVectorStore"/> so the pipelines' capability probe
/// (<c>store is ISparseSearchable</c>) is honest — a dense-only Qdrant store never triggers
/// sparse encoding work.
/// </para>
/// </summary>
public sealed class QdrantSparseVectorStore : QdrantVectorStore, ISparseSearchable
{
    /// <summary>Name of the named sparse vector holding SPLADE weights.</summary>
    internal const string SparseVectorName = "splade";

    public QdrantSparseVectorStore(string host, int port, string collectionName, int vectorDimensions = 1536)
        : base(host, port, collectionName, vectorDimensions)
    {
    }

    /// <summary>
    /// Creates the collection with the sparse vector config when it does not exist. When it
    /// DOES exist, fails fast (<see cref="InvalidOperationException"/>) if it was created
    /// without the "splade" sparse vector — a startup failure beats per-operation warning
    /// spam and a silently diverging point-id scheme.
    /// </summary>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exists = await Client.CollectionExistsAsync(CollectionName, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            await CreateCollectionCoreAsync(CollectionName, VectorDimensions, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var info = await Client.GetCollectionInfoAsync(CollectionName, cancellationToken)
            .ConfigureAwait(false);
        var hasSparseConfig =
            info?.Config?.Params?.SparseVectorsConfig?.Map.ContainsKey(SparseVectorName) == true;
        if (!hasSparseConfig)
        {
            throw new InvalidOperationException(
                $"Qdrant collection '{CollectionName}' was created without sparse vector support " +
                $"(missing named sparse vector '{SparseVectorName}'). Delete the collection and re-ingest " +
                "to enable SPLADE — sparse mode also uses deterministic point ids, so existing points " +
                "cannot be upgraded in place.");
        }
    }

    /// <inheritdoc />
    public async Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default)
    {
        var pointVectors = new List<PointVectors>(items.Count);
        foreach (var (chunk, sparse) in items)
        {
            if (sparse.Count == 0)
                continue; // no terms — nothing to attach

            Vector vector = (sparse.Values.ToArray(), ToUnsignedIndices(sparse));
            pointVectors.Add(new PointVectors
            {
                Id = DeterministicPointId((string)chunk.Chunk.DocumentId, chunk.Chunk.ChunkIndex),
                Vectors = (SparseVectorName, vector),
            });
        }

        if (pointVectors.Count == 0)
            return;

        await Client.UpdateVectorsAsync(CollectionName, pointVectors, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (query.Count == 0)
            return [];

        var results = await Client.QueryAsync(
            CollectionName,
            query: (query.Values.ToArray(), ToUnsignedIndices(query)),
            usingVector: SparseVectorName,
            filter: BuildMetadataFilter(options.MetadataFilter),
            scoreThreshold: (float)options.MinScore,
            limit: (ulong)options.TopK,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results.Select(MapScoredPoint).ToList();
    }

    /// <summary>Sparse mode requires deterministic ids — see the class remarks.</summary>
    private protected override Guid CreatePointId(string documentId, int chunkIndex) =>
        DeterministicPointId(documentId, chunkIndex);

    private protected override async Task CreateCollectionCoreAsync(
        string name, int vectorDimensions, CancellationToken cancellationToken)
    {
        await Client.CreateCollectionAsync(
            name,
            new VectorParams { Size = (ulong)vectorDimensions, Distance = Distance.Cosine },
            sparseVectorsConfig: new SparseVectorConfig
            {
                Map = { [SparseVectorName] = new SparseVectorParams() },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static uint[] ToUnsignedIndices(SparseVector sparse)
    {
        var span = sparse.Indices.Span;
        var indices = new uint[span.Length];
        for (var i = 0; i < span.Length; i++)
            indices[i] = (uint)span[i];
        return indices;
    }

    /// <summary>
    /// Deterministic point id per <c>(DocumentId, ChunkIndex)</c> (SHA-256 truncated to a
    /// GUID with version/variant bits set) so dense upserts and sparse vector updates address
    /// the same point, and re-ingesting a chunk replaces it instead of duplicating it.
    /// PERSISTENT STORAGE CONTRACT: existing collections hold points with these ids — the
    /// algorithm must never change (pinned by a unit test).
    /// </summary>
    internal static Guid DeterministicPointId(string documentId, int chunkIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}\n{chunkIndex}"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x80); // version 8 (custom, RFC 9562)
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(guidBytes);
    }
}
