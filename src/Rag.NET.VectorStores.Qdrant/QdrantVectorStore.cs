using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using Rag.NET.VectorStores;
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
public class QdrantVectorStore : IVectorStore, ICollectionManageable, IChunkLookup, IDisposable
{
    private readonly VectorStoreInitialisationGate _initGate = new();

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

    /// <summary>Runs <see cref="InitializeAsync"/> once, on the first operation that needs the collection (#353).</summary>
    private Task EnsureInitialisedAsync(CancellationToken cancellationToken) =>
        _initGate.EnsureInitialisedAsync(InitializeAsync, cancellationToken);

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", CollectionName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

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

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

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
        // Deliberately no first-use initialisation here: a delete has nothing to delete from a
        // collection that does not exist, and provisioning one to satisfy it would be pure waste —
        // on pgvector an inline HNSW build under a write-blocking lock, triggered by a delete (#353).


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

        // Dropping the bound collection makes "already initialised" false again.
        if (string.Equals(name, CollectionName, StringComparison.Ordinal))
            _initGate.Reset();
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
        {
            Client.Dispose();
            _initGate.Dispose();
        }
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
    /// <summary>
    /// Returns the chunks for the given keys, in one scroll. Missing keys are simply absent (#318).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This cannot be a point-id fetch, which is the difference from every SQL backend.</b>
    /// <see cref="CreatePointId"/> returns <see cref="Guid.NewGuid"/>, so a point's id carries no
    /// relationship to its <c>(document_id, chunk_index)</c> — Qdrant is told the identity only as
    /// payload. The lookup is therefore a filter over that payload, and the id is never consulted.
    /// A subclass overriding <see cref="CreatePointId"/> to something derived does not change this:
    /// the payload is written either way, so this keeps working for both.
    /// </para>
    /// <para>
    /// <b>The pairs are matched as pairs.</b> Each key becomes a nested <c>must</c> of
    /// <c>document_id</c> and <c>chunk_index</c>, and those go into one <c>should</c> — so a key
    /// naming a document that exists at an index that does not cannot pull in another document's
    /// chunk at that index. Filtering on the two fields independently would do exactly that.
    /// </para>
    /// <para>
    /// <b><c>Scroll</c> rather than <c>Query</c>, because there is no query vector.</b> The whole
    /// point of this capability is that the chunks are chosen by graph provenance and no vector
    /// returns them. The limit is the key count: at most one point can match each key, since
    /// <c>(document_id, chunk_index)</c> is unique per stored chunk.
    /// </para>
    /// <para>
    /// <b>Negative chunk indices are ordinary here.</b>
    /// <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to synthetic entity and
    /// relationship chunks, so those are exactly the rows GraphRAG asks for; the payload field is a
    /// signed integer and the condition is equality rather than a range.
    /// </para>
    /// </remarks>
    /// <param name="keys">Chunk identities to fetch.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The chunks that exist, in unspecified order.</returns>
    public async Task<IReadOnlyList<TextChunk>> GetChunksAsync(
        IReadOnlyList<ChunkKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
            return [];

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        var filter = new Filter();
        foreach (var key in keys)
        {
            var pair = new Filter();
            pair.Must.Add(MatchKeyword("document_id", key.DocumentId));
            pair.Must.Add(Match("chunk_index", key.ChunkIndex));
            filter.Should.Add(new Condition { Filter = pair });
        }

        var points = await Client.ScrollAsync(
            CollectionName,
            filter: filter,
            limit: (uint)keys.Count,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var chunks = new List<TextChunk>(keys.Count);
        foreach (var point in points.Result)
            chunks.Add(MapChunk(point.Payload));

        return chunks;
    }

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

    private protected static SearchResult MapScoredPoint(ScoredPoint point) => new()
    {
        Chunk = MapChunk(point.Payload),
        Score = point.Score,
    };

    /// <summary>
    /// Materialises a chunk from a point's payload. Shared by search and by the keyed lookup, which
    /// read different point types — <c>ScoredPoint</c> and <c>RetrievedPoint</c> — but the same
    /// fields, so the two must not drift into disagreeing about what a stored chunk is.
    /// </summary>
    /// <param name="payload">The point's payload.</param>
    /// <returns>The chunk it encodes.</returns>
    private protected static TextChunk MapChunk(
        Google.Protobuf.Collections.MapField<string, Value> payload)
    {
        var documentId = payload["document_id"].StringValue;
        var chunkIndex = (int)payload["chunk_index"].IntegerValue;

        var metadata = payload.TryGetValue("metadata", out var metaValue)
            ? MetadataSerializer.DeserializeMetadataOrThrow(
                metaValue.StringValue,
                $"Qdrant point (document '{documentId}', chunk {chunkIndex}), metadata payload field")
            : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);

        return new TextChunk
        {
            Text = payload["text"].StringValue,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = metadata,
        };
    }
}
