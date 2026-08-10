using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using static Qdrant.Client.Grpc.Conditions;

namespace Rag.NET.Qdrant;

/// <summary>
/// Qdrant-backed <see cref="IVectorStore"/> (dense vectors only). For SPLADE sparse vector
/// support use <see cref="QdrantSparseVectorStore"/> — deliberately a separate type so an
/// <c>is ISparseSearchable</c> capability probe on the registered store is honest: a
/// dense-only Qdrant store never advertises sparse support, and the ingestion/retrieval
/// pipelines skip sparse work entirely instead of computing vectors that could never be
/// stored.
/// </summary>
public class QdrantVectorStore : IVectorStore, ICollectionManageable, IDisposable
{
    private protected QdrantClient Client { get; }
    private protected string CollectionName { get; }
    private protected int VectorDimensions { get; }

    public QdrantVectorStore(string host, int port, string collectionName, int vectorDimensions = 1536)
    {
        Client = new QdrantClient(host, port);
        CollectionName = collectionName;
        VectorDimensions = vectorDimensions;
    }

    public virtual async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exists = await Client.CollectionExistsAsync(CollectionName, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            await CreateCollectionCoreAsync(CollectionName, VectorDimensions, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", CollectionName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        var points = new List<PointStruct>();

        foreach (var chunk in chunks)
        {
            var pointId = CreatePointId((string)chunk.Chunk.DocumentId, chunk.Chunk.ChunkIndex);

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

            // Per-key native copies for server-side filtering; the serialized "metadata" field
            // above stays the round-trip authority. Values keep their type: numbers become
            // Qdrant doubles, booleans booleans; dates ride as sentinel-prefixed ISO strings
            // (Qdrant's gRPC payload has no date type) so they never collide with a plain
            // string that merely looks like a date.
            foreach (var kvp in chunk.Chunk.Metadata)
            {
                points[^1].Payload[$"meta_{kvp.Key}"] = ToPayloadValue(kvp.Value);
            }
        }

        await Client.UpsertAsync(CollectionName, points, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", CollectionName);

        var results = await Client.QueryAsync(
            CollectionName,
            query: queryEmbedding.ToArray(),
            filter: BuildMetadataFilter(options.MetadataFilter),
            scoreThreshold: (float)options.MinScore,
            limit: (ulong)options.TopK,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var mapped = results.Select(MapScoredPoint).ToList();
        activity?.SetTag("vectorstore.result.count", mapped.Count);
        return mapped;
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", CollectionName);

        await Client.DeleteAsync(
            collectionName: CollectionName,
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
        try
        {
            await Client.DeleteCollectionAsync(name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (QdrantException)
        {
            // Qdrant answers result:false when the collection does not exist, which
            // QdrantClient surfaces as a QdrantException. The ICollectionManageable contract
            // makes delete-of-missing a no-op, so absorb exactly that case: if the collection
            // is still there, the delete genuinely failed and the exception stands.
            if (await Client.CollectionExistsAsync(name, cancellationToken).ConfigureAwait(false))
                throw;
        }
    }

    public async Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return await Client.CollectionExistsAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            Client.Dispose();
    }

    /// <summary>
    /// Point id for one chunk. Random by default; <see cref="QdrantSparseVectorStore"/>
    /// overrides with deterministic ids so sparse vectors can address the same points.
    /// </summary>
    private protected virtual Guid CreatePointId(string documentId, int chunkIndex) => Guid.NewGuid();

    /// <summary>
    /// Creates a collection. Dense-only by default; <see cref="QdrantSparseVectorStore"/>
    /// overrides to add the named sparse vector config.
    /// </summary>
    private protected virtual async Task CreateCollectionCoreAsync(
        string name, int vectorDimensions, CancellationToken cancellationToken)
    {
        await Client.CreateCollectionAsync(
            name,
            new VectorParams { Size = (ulong)vectorDimensions, Distance = Distance.Cosine },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>meta_*</c> payload value for one metadata value, keeping its type.</summary>
    private protected static Value ToPayloadValue(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.Number => value.NumberValue,
        MetadataValueKind.Boolean => value.BooleanValue,
        MetadataValueKind.DateTimeOffset => MetadataDateFormat.EncodeSentinel(value.DateTimeOffsetValue),
        _ => value.StringValue,
    };

    /// <summary>
    /// Typed equality conditions per filter pair. Strings and (sentinel-encoded) dates use
    /// keyword match, booleans boolean match; numbers use a closed range (<c>gte = lte</c>)
    /// because Qdrant's match condition has no double form. Metadata stored before values
    /// carried types is all strings, so non-string filters do not match it until re-ingested.
    /// </summary>
    private protected static Filter? BuildMetadataFilter(IDictionary<string, MetadataValue>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        var filter = new Filter();
        foreach (var kvp in metadataFilter)
        {
            var field = $"meta_{kvp.Key}";
            filter.Must.Add(kvp.Value.Kind switch
            {
                MetadataValueKind.Number => Range(field, new global::Qdrant.Client.Grpc.Range
                {
                    Gte = kvp.Value.NumberValue,
                    Lte = kvp.Value.NumberValue,
                }),
                MetadataValueKind.Boolean => Match(field, kvp.Value.BooleanValue),
                MetadataValueKind.DateTimeOffset => MatchKeyword(field, MetadataDateFormat.EncodeSentinel(kvp.Value.DateTimeOffsetValue)),
                _ => MatchKeyword(field, kvp.Value.StringValue),
            });
        }

        return filter;
    }

    private protected static SearchResult MapScoredPoint(ScoredPoint point)
    {
        Dictionary<string, MetadataValue> metadata;
        if (point.Payload.TryGetValue("metadata", out var metaValue))
        {
            var metadataResult = MetadataSerializer.DeserializeMetadata(metaValue.StringValue);
            metadata = metadataResult.IsSuccess
                ? metadataResult.Value
                : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        }
        else
        {
            metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        }

        return new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = point.Payload["text"].StringValue,
                DocumentId = new DocumentId(point.Payload["document_id"].StringValue),
                ChunkIndex = (int)point.Payload["chunk_index"].IntegerValue,
                Metadata = metadata,
            },
            Score = point.Score,
        };
    }
}
