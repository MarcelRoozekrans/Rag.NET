using System.Text.Json;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using static Qdrant.Client.Grpc.Conditions;

namespace Rag.NET.Qdrant;

public sealed class QdrantVectorStore : IVectorStore, ICollectionManageable, IDisposable
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly int _vectorDimensions;

    public QdrantVectorStore(string host, int port, string collectionName, int vectorDimensions = 1536)
    {
        _client = new QdrantClient(host, port);
        _collectionName = collectionName;
        _vectorDimensions = vectorDimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exists = await _client.CollectionExistsAsync(_collectionName, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            await _client.CreateCollectionAsync(
                _collectionName,
                new VectorParams { Size = (ulong)_vectorDimensions, Distance = Distance.Cosine },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var points = new List<PointStruct>();

        foreach (var chunk in chunks)
        {
            var pointId = Guid.NewGuid();

            points.Add(new PointStruct
            {
                Id = pointId,
                Vectors = chunk.Embedding.ToArray(),
                Payload =
                {
                    ["text"] = chunk.Chunk.Text,
                    ["document_id"] = chunk.Chunk.DocumentId,
                    ["chunk_index"] = chunk.Chunk.ChunkIndex,
                    ["metadata"] = JsonSerializer.Serialize(chunk.Chunk.Metadata),
                },
            });
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
            limit: (ulong)options.TopK,
            scoreThreshold: (float)options.MinScore,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results
            .Select(point =>
            {
                var metadata = point.Payload.TryGetValue("metadata", out var metaValue)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(metaValue.StringValue) ?? []
                    : [];

                return new SearchResult
                {
                    Chunk = new TextChunk
                    {
                        Text = point.Payload["text"].StringValue,
                        DocumentId = point.Payload["document_id"].StringValue,
                        ChunkIndex = (int)point.Payload["chunk_index"].IntegerValue,
                        Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                    },
                    Score = point.Score,
                };
            })
            .ToList();
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
        await _client.CreateCollectionAsync(
            name,
            new VectorParams { Size = (ulong)vectorDimensions, Distance = Distance.Cosine },
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
}
