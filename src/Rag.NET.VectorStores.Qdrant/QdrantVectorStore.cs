using System.Security.Cryptography;
using System.Text;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using static Qdrant.Client.Grpc.Conditions;
// The Qdrant client also declares a Grpc SparseVector message; the Rag.NET contract type wins.
using SparseVector = Rag.NET.Models.SparseVector;

namespace Rag.NET.Qdrant;

/// <summary>
/// Qdrant-backed <see cref="IVectorStore"/>. When constructed with
/// <c>enableSparseVectors: true</c> it also serves <see cref="ISparseSearchable"/> through a
/// named sparse vector ("splade") stored on the same points as the dense vectors: point ids
/// become deterministic (derived from <c>DocumentId</c>/<c>ChunkIndex</c>, making
/// <see cref="StoreAsync"/> idempotent per chunk) so <see cref="StoreSparseAsync"/> can attach
/// sparse vectors to points upserted by <see cref="StoreAsync"/> — call
/// <see cref="StoreAsync"/> first, as ingestion does.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore, ISparseSearchable, ICollectionManageable, IDisposable
{
    /// <summary>Name of the named sparse vector holding SPLADE weights.</summary>
    internal const string SparseVectorName = "splade";

    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly int _vectorDimensions;
    private readonly bool _enableSparseVectors;

    public QdrantVectorStore(
        string host, int port, string collectionName, int vectorDimensions = 1536,
        bool enableSparseVectors = false)
    {
        _client = new QdrantClient(host, port);
        _collectionName = collectionName;
        _vectorDimensions = vectorDimensions;
        _enableSparseVectors = enableSparseVectors;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exists = await _client.CollectionExistsAsync(_collectionName, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            await CreateCollectionCoreAsync(_collectionName, _vectorDimensions, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var points = new List<PointStruct>();

        foreach (var chunk in chunks)
        {
            // Sparse mode requires deterministic ids so StoreSparseAsync can address the
            // same points; the legacy random-id behavior is preserved otherwise.
            var pointId = _enableSparseVectors
                ? DeterministicPointId((string)chunk.Chunk.DocumentId, chunk.Chunk.ChunkIndex)
                : Guid.NewGuid();

            points.Add(new PointStruct
            {
                Id = pointId,
                Vectors = chunk.Embedding.ToArray(),
                Payload =
                {
                    ["text"] = chunk.Chunk.Text,
                    ["document_id"] = (string)chunk.Chunk.DocumentId,
                    ["chunk_index"] = chunk.Chunk.ChunkIndex,
                    ["metadata"] = MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata),
                },
            });

            foreach (var kvp in chunk.Chunk.Metadata)
            {
                points[^1].Payload[$"meta_{kvp.Key}"] = kvp.Value;
            }
        }

        await _client.UpsertAsync(_collectionName, points, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = await _client.SearchAsync(
            _collectionName,
            queryEmbedding.ToArray(),
            filter: BuildMetadataFilter(options.MetadataFilter),
            limit: (ulong)options.TopK,
            scoreThreshold: (float)options.MinScore,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results.Select(MapScoredPoint).ToList();
    }

    /// <inheritdoc />
    public async Task StoreSparseAsync(
        IReadOnlyList<(EmbeddedChunk Chunk, SparseVector Sparse)> items,
        CancellationToken cancellationToken = default)
    {
        EnsureSparseEnabled();

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

        await _client.UpdateVectorsAsync(_collectionName, pointVectors, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchSparseAsync(
        SparseVector query,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureSparseEnabled();

        if (query.Count == 0)
            return [];

        var results = await _client.QueryAsync(
            _collectionName,
            query: (query.Values.ToArray(), ToUnsignedIndices(query)),
            usingVector: SparseVectorName,
            filter: BuildMetadataFilter(options.MetadataFilter),
            scoreThreshold: (float)options.MinScore,
            limit: (ulong)options.TopK,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results.Select(MapScoredPoint).ToList();
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        await _client.DeleteAsync(
            collectionName: _collectionName,
            filter: MatchKeyword("document_id", documentId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        await CreateCollectionCoreAsync(name, vectorDimensions, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await _client.DeleteCollectionAsync(name, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return await _client.CollectionExistsAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private async Task CreateCollectionCoreAsync(string name, int vectorDimensions, CancellationToken cancellationToken)
    {
        var vectorParams = new VectorParams { Size = (ulong)vectorDimensions, Distance = Distance.Cosine };
        if (_enableSparseVectors)
        {
            await _client.CreateCollectionAsync(
                name,
                vectorParams,
                sparseVectorsConfig: new SparseVectorConfig
                {
                    Map = { [SparseVectorName] = new SparseVectorParams() },
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        await _client.CreateCollectionAsync(name, vectorParams, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureSparseEnabled()
    {
        if (!_enableSparseVectors)
        {
            throw new InvalidOperationException(
                "Sparse vector support is disabled for this QdrantVectorStore. " +
                "Construct it with enableSparseVectors: true (UseQdrant(..., enableSparseVectors: true)) " +
                "so the collection is created with the named sparse vector config.");
        }
    }

    private static Filter? BuildMetadataFilter(IDictionary<string, string>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        var filter = new Filter();
        foreach (var kvp in metadataFilter)
        {
            filter.Must.Add(MatchKeyword($"meta_{kvp.Key}", kvp.Value));
        }

        return filter;
    }

    private static SearchResult MapScoredPoint(ScoredPoint point)
    {
        Dictionary<string, string> metadata;
        if (point.Payload.TryGetValue("metadata", out var metaValue))
        {
            var metadataResult = MetadataSerializer.DeserializeMetadata(metaValue.StringValue);
            metadata = metadataResult.IsSuccess
                ? metadataResult.Value
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = point.Payload["text"].StringValue,
                DocumentId = new DocumentId(point.Payload["document_id"].StringValue),
                ChunkIndex = (int)point.Payload["chunk_index"].IntegerValue,
                Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            },
            Score = point.Score,
        };
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
