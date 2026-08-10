using System.Diagnostics;
using Pinecone;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using PineconeIndexModel = Pinecone.Index;
using PineconeMetadataValue = Pinecone.MetadataValue;
using RagMetadataValue = Rag.NET.Models.MetadataValue;

namespace Rag.NET.Pinecone;

/// <summary>
/// Pinecone-backed <see cref="IVectorStore"/> with serverless-index lifecycle management
/// (<see cref="ICollectionManageable"/>), built on the official <c>Pinecone.Client</c> SDK.
/// Dense-only — the opt-in <see cref="PineconeSparseVectorStore"/> subtype adds
/// <c>ISparseSearchable</c>. Backend and transport errors throw — the pipeline layer owns
/// degradation (house store posture).
/// <para>
/// Record ids are <c>{documentId}:{chunkIndex}</c>, so re-storing a chunk upserts
/// (replaces) it. Pinecone stores no document body: the chunk text lives in metadata
/// (key <c>text</c>) next to <c>document_id</c> and <c>chunk_index</c>, and is read back
/// into <see cref="SearchResult"/>. Note Pinecone's ~40 KB metadata limit per record.
/// Queries return native similarity scores (cosine similarity for the default metric),
/// so <c>MinScore</c> applies directly; <c>TopK</c> maps to the query's <c>topK</c>.
/// An optional <see cref="PineconeOptions.Namespace"/> scopes every operation.
/// </para>
/// <para>
/// The data-plane client is resolved on first use by describing the configured index
/// (fail-fast when it does not exist) and cached; create/delete of that index through
/// this store invalidates the cache.
/// </para>
/// <para>
/// Deliberately NOT <see cref="IDisposable"/> (unlike the sibling stores that own an
/// <c>HttpClient</c>): the SDK's <c>PineconeClient</c>/<c>IndexClient</c> implement no
/// disposal interface, so there is nothing to release.
/// </para>
/// </summary>
public class PineconeVectorStore : IVectorStore, ICollectionManageable
{
    /// <summary>
    /// Records per upsert request: 1536-dim embeddings weigh ~6 KB each, so 100 stays
    /// far below the 4 MB gRPC message limit while amortizing round-trips.
    /// </summary>
    private const int UpsertBatchSize = 100;

    /// <summary>Pinecone's documented cap on ids per delete request.</summary>
    private const int DeleteBatchSize = 1000;

    /// <summary>
    /// Ids per list-by-prefix page while collecting a document's chunks for deletion.
    /// Matches Pinecone's own default; sent explicitly so the paging contract does not
    /// depend on an unstated server default.
    /// </summary>
    private const uint ListPageSize = 100;

    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly PineconeClient _client;
    private readonly PineconeOptions _options;
    private IndexClient? _index;
    private int _resolvedDimensions;

    public PineconeVectorStore(PineconeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("PineconeOptions.ApiKey must be set.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.IndexName))
            throw new ArgumentException("PineconeOptions.IndexName must be set.", nameof(options));
        _options = options;
        _client = CreateClient(options);
    }

    /// <summary>The configured index name (used by subclass error messages).</summary>
    private protected string IndexName => _options.IndexName;

    /// <summary>
    /// Dense dimensions of the live index, learned from the describe in
    /// <see cref="GetIndexAsync"/> — the authority for anything sent to the data plane
    /// (the sparse subtype's zero dense vector). Falls back to the configured
    /// <see cref="PineconeOptions.VectorDimensions"/> until the index has been resolved,
    /// or when the index reports none (sparse-only indexes).
    /// </summary>
    private protected int VectorDimensions =>
        Volatile.Read(ref _resolvedDimensions) is var resolved and > 0
            ? resolved
            : _options.VectorDimensions;

    /// <summary>Configured namespace, shared with the sparse subtype's queries.</summary>
    private protected string? Namespace => _options.Namespace;

    /// <summary>
    /// Metric for indexes created by <see cref="CreateCollectionAsync"/> — cosine here;
    /// the sparse subtype overrides with dotproduct (the only metric that accepts
    /// sparse values).
    /// </summary>
    private protected virtual CreateIndexRequestMetric IndexMetric => CreateIndexRequestMetric.Cosine;

    /// <inheritdoc />
    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", IndexName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        if (chunks.Count == 0)
            return;

        var vectors = new List<Vector>(chunks.Count);
        foreach (var chunk in chunks)
            vectors.Add(BuildVector(chunk, sparse: null));
        await UpsertBatchedAsync(vectors, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", IndexName);

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var response = await index.QueryAsync(
            new QueryRequest
            {
                Vector = queryEmbedding,
                TopK = (uint)options.TopK,
                Filter = BuildFilter(options.MetadataFilter),
                IncludeMetadata = true,
                Namespace = _options.Namespace,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = MapMatches(response, options.MinScore);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    /// <summary>
    /// Deletes every chunk of the document via list-vector-ids-by-prefix
    /// (<c>{documentId}:</c>) + delete-by-ids. Pinned path: serverless indexes do not
    /// support delete-by-metadata-filter — both the real service and Pinecone Local
    /// answer "Serverless and Starter indexes do not support deleting with metadata
    /// filtering" (verified empirically 2026-07-26).
    /// </summary>
    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", IndexName);

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var ids = await ListChunkIdsAsync(index, documentId, cancellationToken).ConfigureAwait(false);
        for (var offset = 0; offset < ids.Count; offset += DeleteBatchSize)
        {
            var count = Math.Min(DeleteBatchSize, ids.Count - offset);
            await index.DeleteAsync(
                new DeleteRequest { Ids = ids.GetRange(offset, count), Namespace = _options.Namespace },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a serverless index (cloud/region from <see cref="PineconeOptions"/>;
    /// metric per <see cref="IndexMetric"/>) and polls describe until it reports ready,
    /// bounded by <see cref="PineconeOptions.IndexReadyTimeout"/>.
    /// </summary>
    public async Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorDimensions);

        var model = await _client.CreateIndexAsync(
            new CreateIndexRequest
            {
                Name = name,
                Dimension = vectorDimensions,
                Metric = IndexMetric,
                Spec = new ServerlessIndexSpec
                {
                    Serverless = new ServerlessSpec { Cloud = _options.Cloud, Region = _options.Region },
                },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await WaitUntilReadyAsync(name, model, cancellationToken).ConfigureAwait(false);
        InvalidateCachedIndex(name);
    }

    /// <inheritdoc />
    public async Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            await _client.DeleteIndexAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (NotFoundError)
        {
            // Delete-of-missing is a no-op (the ICollectionManageable contract).
        }

        InvalidateCachedIndex(name);
    }

    /// <inheritdoc />
    public async Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            _ = await _client.DescribeIndexAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (NotFoundError)
        {
            return false;
        }
    }

    /// <summary>Record id per <c>(DocumentId, ChunkIndex)</c> — plain string, upsert = replace.</summary>
    internal static string RecordId(string documentId, int chunkIndex) =>
        FormattableString.Invariant($"{documentId}:{chunkIndex}");

    /// <summary>
    /// Hook for subtypes to reject an incompatible index before the data-plane client is
    /// cached (the sparse subtype requires the dotproduct metric). Runs on every failed
    /// first use — nothing is cached until it passes.
    /// </summary>
    private protected virtual void ValidateIndexModel(PineconeIndexModel model)
    {
    }

    /// <summary>
    /// Resolves the data-plane client for the configured index once and caches it: a
    /// single describe pins the index host (and lets <see cref="ValidateIndexModel"/>
    /// fail fast), then the client connects straight to that host. Concurrent first
    /// calls may race the describe; one result wins the cache — benign.
    /// </summary>
    private protected async Task<IndexClient> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _index) is { } cached)
            return cached;

        PineconeIndexModel model;
        try
        {
            model = await _client.DescribeIndexAsync(_options.IndexName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotFoundError exception)
        {
            throw new InvalidOperationException(
                $"Pinecone index '{_options.IndexName}' does not exist. Create it first — e.g. via " +
                "ICollectionManageable.CreateCollectionAsync — before storing or searching.",
                exception);
        }

        ValidateIndexModel(model);
        // The index is the authority on its dimensions — a configured VectorDimensions
        // that disagrees would otherwise break every generated vector (e.g. the sparse
        // subtype's zero dense vector). Published before the client so any reader that
        // sees the cached client also sees the dimensions.
        if (model.Dimension is > 0 and var dimension)
            Volatile.Write(ref _resolvedDimensions, dimension);
        var resolved = _client.Index(name: _options.IndexName, host: model.Host);
        Interlocked.CompareExchange(ref _index, resolved, null);
        return Volatile.Read(ref _index) ?? resolved;
    }

    private protected async Task UpsertBatchedAsync(List<Vector> vectors, CancellationToken cancellationToken)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        for (var offset = 0; offset < vectors.Count; offset += UpsertBatchSize)
        {
            var count = Math.Min(UpsertBatchSize, vectors.Count - offset);
            await index.UpsertAsync(
                new UpsertRequest { Vectors = vectors.GetRange(offset, count), Namespace = _options.Namespace },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private protected Vector BuildVector(EmbeddedChunk chunk, SparseValues? sparse)
    {
        var textChunk = chunk.Chunk;
        // Pinecone metadata values are natively typed (string/number/bool, numbers stored as
        // float64) but exclude objects, so dates ride as sentinel-prefixed ISO strings and are
        // decoded on read.
        var metadata = new Metadata();
        foreach (var kvp in textChunk.Metadata)
            metadata[kvp.Key] = ToPineconeValue(kvp.Value);
        // Written last so the reserved keys always win over same-named chunk metadata.
        metadata["document_id"] = (string)textChunk.DocumentId;
        metadata["chunk_index"] = textChunk.ChunkIndex;
        metadata["text"] = textChunk.Text;

        return new Vector
        {
            Id = RecordId((string)textChunk.DocumentId, textChunk.ChunkIndex),
            Values = chunk.Embedding,
            SparseValues = sparse,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Pinecone filter: typed <c>$eq</c> per key, <c>$and</c>-composed for two or more keys. The
    /// <c>$eq</c> operand carries the same native type <see cref="ToPineconeValue"/> stored, so a
    /// Number filter matches a stored number and never a same-looking string. Metadata stored
    /// before values carried types is all strings, so non-string filters do not match it until
    /// re-ingested.
    /// </summary>
    private protected static Metadata? BuildFilter(IDictionary<string, RagMetadataValue>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        if (metadataFilter.Count == 1)
        {
            foreach (var kvp in metadataFilter)
                return new Metadata { [kvp.Key] = EqualityClause(kvp.Value) };
        }

        var operands = new PineconeMetadataValue?[metadataFilter.Count];
        var i = 0;
        foreach (var kvp in metadataFilter)
            operands[i++] = new Metadata { [kvp.Key] = EqualityClause(kvp.Value) };
        return new Metadata { ["$and"] = operands };
    }

    private protected IReadOnlyList<SearchResult> MapMatches(QueryResponse response, double minScore)
    {
        var matches = response.Matches ?? [];
        var results = new List<SearchResult>(matches.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var match in matches)
        {
            var score = (double)(match.Score ?? 0f);
            if (score < minScore)
                continue;

            results.Add(BuildResult(match, score));
        }

        return results;
    }

    private static Metadata EqualityClause(RagMetadataValue value) => new() { ["$eq"] = ToPineconeValue(value) };

    /// <summary>The native Pinecone metadata value for one metadata value, keeping its type.</summary>
    private static PineconeMetadataValue ToPineconeValue(RagMetadataValue value) => value.Kind switch
    {
        MetadataValueKind.Number => value.NumberValue,
        MetadataValueKind.Boolean => value.BooleanValue,
        MetadataValueKind.DateTimeOffset => MetadataDateFormat.EncodeSentinel(value.DateTimeOffsetValue),
        _ => value.StringValue,
    };

    /// <summary>
    /// Maps a Pinecone metadata value back to a <see cref="RagMetadataValue"/> with its kind:
    /// numbers and booleans directly, sentinel-encoded strings to dates, all other strings —
    /// including every value stored before metadata carried types — as strings.
    /// </summary>
    private static RagMetadataValue FromPineconeValue(PineconeMetadataValue value)
    {
        if (value.IsT1)
            return value.AsT1;
        if (value.IsT2)
            return value.AsT2;
        if (!value.IsT0)
            return value.ToString() ?? string.Empty;

        return MetadataDateFormat.TryDecodeSentinel(value.AsT0, out var date)
            ? date
            : (RagMetadataValue)value.AsT0;
    }

    /// <summary>
    /// Missing <c>document_id</c>/<c>chunk_index</c> metadata is backend corruption — the
    /// store posture is to throw naming the record, never to guess from the record id
    /// (document ids may themselves contain the <c>:</c> separator).
    /// </summary>
    private static SearchResult BuildResult(ScoredVector match, double score)
    {
        var metadata = match.Metadata;
        if (metadata is null
            || !metadata.TryGetValue("document_id", out var documentId) || documentId?.IsT0 != true
            || !metadata.TryGetValue("chunk_index", out var chunkIndex) || chunkIndex?.IsT1 != true)
        {
            throw new InvalidOperationException(
                $"Pinecone record '{match.Id}' is missing its document_id/chunk_index metadata.");
        }

        var text = metadata.TryGetValue("text", out var textValue) && textValue?.IsT0 == true
            ? textValue.AsT0
            : string.Empty;
        var chunkMetadata = new Dictionary<string, RagMetadataValue>(StringComparer.Ordinal);
        foreach (var kvp in metadata)
        {
            if (kvp.Key is "document_id" or "chunk_index" or "text")
                continue;
            if (kvp.Value is { } value)
                chunkMetadata[kvp.Key] = FromPineconeValue(value);
        }

        return new SearchResult
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId(documentId.AsT0),
                ChunkIndex = (int)chunkIndex.AsT1,
                Metadata = chunkMetadata,
            },
            Score = score,
        };
    }

    private async Task<List<string>> ListChunkIdsAsync(
        IndexClient index, string documentId, CancellationToken cancellationToken)
    {
        var prefix = documentId + ":";
        var ids = new List<string>();
        string? paginationToken = null;
        do
        {
            var page = await index.ListAsync(
                new ListRequest
                {
                    Prefix = prefix,
                    Namespace = _options.Namespace,
                    PaginationToken = paginationToken,
                    // Stated, not inherited from the server default (which happens to be
                    // the same): the page size is what the >100-chunk deletion test pins.
                    Limit = ListPageSize,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Vectors ?? [])
            {
                if (item.Id is { } id && IsChunkOfDocument(id, prefix))
                    ids.Add(id);
            }

            paginationToken = page.Pagination?.Next;
        }
        // Empty string, not just null, ends the listing: Pinecone's last page may carry a
        // present-but-blank pagination token, which would otherwise loop forever.
        while (!string.IsNullOrEmpty(paginationToken));
        return ids;
    }

    /// <summary>
    /// Exact-document guard on the prefix listing: ids are <c>{documentId}:{chunkIndex}</c>,
    /// so for document <c>a</c> the prefix <c>a:</c> would also match chunks of a document
    /// named <c>a:b</c> (ids <c>a:b:{i}</c>). A candidate belongs to the target document
    /// iff the remainder after the prefix is purely digits — a remainder containing the
    /// <c>:</c> separator (or any non-digit) belongs to a longer document id.
    /// </summary>
    private static bool IsChunkOfDocument(string candidateId, string prefix)
    {
        if (candidateId.Length <= prefix.Length)
            return false;

        for (var i = prefix.Length; i < candidateId.Length; i++)
        {
            if (!char.IsAsciiDigit(candidateId[i]))
                return false;
        }

        return true;
    }

    private async Task WaitUntilReadyAsync(
        string name, PineconeIndexModel model, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (model.Status?.Ready != true)
        {
            if (Stopwatch.GetElapsedTime(start) >= _options.IndexReadyTimeout)
            {
                throw new InvalidOperationException(
                    $"Pinecone index '{name}' did not become ready within {_options.IndexReadyTimeout} " +
                    $"(last state: {model.Status?.State.ToString() ?? "unknown"}).");
            }

            await Task.Delay(ReadyPollInterval, cancellationToken).ConfigureAwait(false);
            model = await _client.DescribeIndexAsync(name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Create/delete of the configured index makes any cached host stale.</summary>
    private void InvalidateCachedIndex(string name)
    {
        if (string.Equals(name, _options.IndexName, StringComparison.Ordinal))
            Volatile.Write(ref _index, null);
    }

    private static PineconeClient CreateClient(PineconeOptions options)
    {
        if (options.Endpoint is null)
            return new PineconeClient(options.ApiKey);

        // Endpoint override = Pinecone Local: BaseUrl points the control plane at the
        // emulator, and an http scheme disables TLS on the data-plane gRPC channels.
        return new PineconeClient(options.ApiKey, new ClientOptions
        {
            BaseUrl = options.Endpoint.ToString().TrimEnd('/'),
            IsTlsEnabled = string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
        });
    }
}
