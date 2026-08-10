using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;
using ZeroAlloc.Rest;
using ZeroAlloc.Results;

namespace Rag.NET.Weaviate;

/// <summary>
/// Weaviate-backed <see cref="IVectorStore"/> with native BM25+vector hybrid search
/// (<see cref="IHybridSearchable"/>) and class lifecycle management
/// (<see cref="ICollectionManageable"/>). Transport is REST for schema/writes and a single
/// GraphQL POST for search. Backend and transport errors throw — the pipeline layer owns
/// degradation (house store posture).
/// <para>
/// Score semantics (pinned 2026-07-25 against Weaviate 1.39, cosine distance):
/// dense search reads <c>_additional.distance</c> — cosine distance in [0, 2] where 0 is
/// identical — and maps <c>Score = 1 - distance / 2</c> (identical vector ⇒ 1, equal to
/// Weaviate's <c>certainty</c>); hybrid search reads <c>_additional.score</c>, Weaviate's
/// relative-score-fusion value in [0, 1] returned as a JSON <em>string</em>.
/// <c>MinScore</c> is applied to the mapped score, <c>TopK</c> becomes <c>limit</c>.
/// </para>
/// </summary>
public sealed class WeaviateVectorStore : IVectorStore, IHybridSearchable, ICollectionManageable, IDisposable
{
    private readonly IWeaviateApi _api;
    private readonly WeaviateOptions _options;
    private readonly HttpClient? _ownedHttpClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public WeaviateVectorStore(WeaviateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Endpoint is null)
            throw new ArgumentException("WeaviateOptions.Endpoint must be set.", nameof(options));
        _options = options;
        _ownedHttpClient = CreateHttpClient(options);
        _api = new WeaviateApiClient(_ownedHttpClient, new ZeroAlloc.Rest.SystemTextJson.SystemTextJsonSerializer());
    }

    /// <summary>Test seam: inject a fake <see cref="IWeaviateApi"/>.</summary>
    internal WeaviateVectorStore(IWeaviateApi api, WeaviateOptions options)
    {
        _api = api;
        _options = options;
    }

    /// <summary>
    /// Creates the configured class (and tenant, when set) if missing. Also invoked lazily
    /// by <see cref="StoreAsync"/> so a forgotten initialization can never let auto-schema
    /// create the class with the wrong <c>document_id</c> tokenization.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        EnsureInitializedAsync(cancellationToken);

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.ClassName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        if (chunks.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var objects = new List<WeaviateObject>(chunks.Count);
        foreach (var chunk in chunks)
        {
            objects.Add(new WeaviateObject
            {
                Class = _options.ClassName,
                Id = DeterministicObjectId((string)chunk.Chunk.DocumentId, chunk.Chunk.ChunkIndex),
                Vector = chunk.Embedding.ToArray(),
                Tenant = _options.Tenant,
                Properties = BuildProperties(chunk.Chunk),
            });
        }

        var result = await _api.BatchObjectsAsync(
            new WeaviateBatchObjectsRequest { Objects = objects },
            cancellationToken).ConfigureAwait(false);
        ThrowOnBatchFailures(Unwrap(result, "batch store"));
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.ClassName);

        var query = BuildGetQuery(
            $"nearVector: {{vector: {FormatVector(queryEmbedding.Span)}}}",
            options,
            additionalField: "distance");
        var hits = await ExecuteGetQueryAsync(query, cancellationToken).ConfigureAwait(false);
        var results = MapResults(hits, ScoreFromDistance, options.MinScore);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.ClassName);
        activity?.SetTag("vectorstore.hybrid", true);

        // properties: ["text"] scopes the BM25 side to the chunk text (the design contract);
        // without it Weaviate would also keyword-score the word-tokenized meta_* properties.
        var query = BuildGetQuery(
            $"hybrid: {{query: {GraphQlString(textQuery)}, vector: {FormatVector(queryEmbedding.Span)}, properties: [\"text\"]}}",
            options,
            additionalField: "score");
        var hits = await ExecuteGetQueryAsync(query, cancellationToken).ConfigureAwait(false);
        var results = MapResults(hits, ScoreFromHybridScore, options.MinScore);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _options.ClassName);

        var request = new WeaviateBatchDeleteRequest
        {
            Match = new WeaviateBatchDeleteMatch
            {
                Class = _options.ClassName,
                Where = new WeaviateWhereFilter
                {
                    Path = ["document_id"],
                    Operator = "Equal",
                    ValueText = documentId,
                },
            },
        };

        // Weaviate caps every batch delete at results.limit objects (default 10,000) and
        // reports per-call counters in a 200 response — so a partial failure or an over-cap
        // document would otherwise vanish silently. Loop until a pass matches fewer objects
        // than the cap, and throw as soon as any pass reports failures.
        while (true)
        {
            var result = await _api.BatchDeleteAsync(request, _options.Tenant, cancellationToken)
                .ConfigureAwait(false);
            var counters = Unwrap(result, "batch delete").Results;
            if (counters is null)
                return; // no counters to verify — nothing more actionable

            if (counters.Failed > 0)
            {
                throw new InvalidOperationException(
                    $"Weaviate batch delete for document '{documentId}' failed for " +
                    $"{counters.Failed} of {counters.Matches} matched object(s).");
            }

            // Fewer matches than the cap ⇒ this pass saw the tail of the document. The
            // Successful == 0 guard makes an (unexpected) zero-progress pass terminate
            // instead of looping forever.
            if (counters.Matches < counters.Limit || counters.Successful == 0)
                return;
        }
    }

    public async Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Weaviate infers dimensions from the first stored vector (vectorizer: none) — the
        // argument is validated for interface-contract sanity but not sent.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorDimensions);

        var result = await _api.CreateClassAsync(BuildClassSchema(name), cancellationToken)
            .ConfigureAwait(false);
        Unwrap(result, $"create class '{name}'");

        if (_options.Tenant is { } tenant)
            await CreateTenantAsync(name, tenant, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _api.DeleteClassAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var result = await _api.GetClassAsync(name, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
            return true;
        if (result.Error.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        throw new InvalidOperationException(
            $"Weaviate class probe for '{name}' failed with {FormatHttpError(result.Error)}.");
    }

    public void Dispose()
    {
        _initLock.Dispose();
        _ownedHttpClient?.Dispose();
    }

    /// <summary>
    /// Deterministic object id per <c>(DocumentId, ChunkIndex)</c> so re-storing a chunk
    /// replaces its object instead of duplicating it. Delegates to the shared
    /// <see cref="DeterministicChunkId"/> derivation (also used by the Qdrant sparse store) —
    /// see its PERSISTENT STORAGE CONTRACT; the pinned-value unit test lives in this store's
    /// suite.
    /// </summary>
    internal static Guid DeterministicObjectId(string documentId, int chunkIndex) =>
        DeterministicChunkId.Derive(documentId, chunkIndex);

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        // The store is a singleton: without the lock, two concurrent first writes would race
        // the exists-probe and the loser's CreateCollectionAsync would throw a spurious 422.
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var exists = await CollectionExistsAsync(_options.ClassName, cancellationToken)
                .ConfigureAwait(false);
            if (!exists)
            {
                await CreateCollectionAsync(_options.ClassName, _options.VectorDimensions, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (_options.Tenant is { } tenant)
            {
                // Tenant creation is idempotent (verified against Weaviate 1.39: re-POSTing an
                // existing tenant answers 200), so an existing class can safely gain the tenant.
                await CreateTenantAsync(_options.ClassName, tenant, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task CreateTenantAsync(string className, string tenant, CancellationToken cancellationToken)
    {
        var result = await _api.CreateTenantsAsync(
            className,
            [new WeaviateTenant { Name = tenant }],
            cancellationToken).ConfigureAwait(false);
        Unwrap(result, $"create tenant '{tenant}' on class '{className}'");
    }

    private WeaviateClass BuildClassSchema(string name) => new()
    {
        Class = name,
        Vectorizer = "none",
        VectorIndexConfig = new WeaviateVectorIndexConfig { Distance = "cosine" },
        Properties =
        [
            // "field" tokenization: Equal filters must match the whole document id, not its
            // word tokens (word tokenization would let "doc-1" match a filter for "doc-2"'s
            // shared "doc" token during delete-by-document).
            new WeaviateProperty { Name = "document_id", DataType = ["text"], Tokenization = "field" },
            new WeaviateProperty { Name = "chunk_index", DataType = ["int"] },
            new WeaviateProperty { Name = "text", DataType = ["text"] },
            // Serialized copy of the chunk metadata for lossless round-tripping (GraphQL
            // cannot select unknown auto-schema properties back); the per-key meta_* copies
            // written by StoreAsync exist purely for server-side where filtering.
            new WeaviateProperty { Name = "metadata_json", DataType = ["text"], Tokenization = "field" },
        ],
        MultiTenancyConfig = _options.Tenant is null ? null : new WeaviateMultiTenancyConfig { Enabled = true },
    };

    private static IDictionary<string, object?> BuildProperties(TextChunk chunk)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["document_id"] = (string)chunk.DocumentId,
            ["chunk_index"] = chunk.ChunkIndex,
            ["text"] = chunk.Text,
            ["metadata_json"] = MetadataSerializer.SerializeMetadata(chunk.Metadata),
        };

        // Metadata keys become meta_-prefixed properties via Weaviate auto-schema (enabled by
        // default — verified: new properties are accepted without any env toggle), making them
        // server-side filterable like Qdrant's meta_ payload fields. Values keep their type:
        // numbers infer as `number`, booleans as `boolean`; dates ride as sentinel-prefixed
        // ISO strings (auto-schema would otherwise infer plain `text` and lose the kind).
        // NOTE for classes written before values carried types: their meta_* properties are
        // all `text`, so storing a number/boolean under the same key now fails loudly in the
        // batch response — recreate the class (re-ingesting) to type such keys.
        foreach (var kvp in chunk.Metadata)
            properties[$"meta_{kvp.Key}"] = ToPropertyValue(kvp.Value);

        return properties;
    }

    /// <summary>The <c>meta_*</c> property value for one metadata value, keeping its type.</summary>
    private static object? ToPropertyValue(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.Number => value.NumberValue,
        MetadataValueKind.Boolean => value.BooleanValue,
        MetadataValueKind.DateTimeOffset => MetadataDateFormat.EncodeSentinel(value.DateTimeOffsetValue),
        _ => value.StringValue,
    };

    private async Task<JsonElement> ExecuteGetQueryAsync(string query, CancellationToken cancellationToken)
    {
        var result = await _api.QueryAsync(new WeaviateGraphQlRequest(query), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            // The realistic non-2xx here is a fresh backend: a Weaviate instance whose
            // schema contains no classes at all rejects EVERY GraphQL request with HTTP 422
            // ("no graphql provider present") — searching before initializing/ingesting.
            throw new InvalidOperationException(
                $"Weaviate GraphQL query against class '{_options.ClassName}' failed with " +
                $"{FormatHttpError(result.Error)}. A Weaviate instance with no classes rejects " +
                "all GraphQL queries — is the store initialized and has anything been ingested?");
        }

        var response = result.Value;

        if (response.Errors is { Count: > 0 } errors)
        {
            var messages = new string[errors.Count];
            for (var i = 0; i < errors.Count; i++)
                messages[i] = errors[i].Message ?? "unknown error";
            throw new InvalidOperationException(
                $"Weaviate GraphQL query failed: {string.Join("; ", messages)}");
        }

        // The Get payload is keyed by class name (data.Get.{Class}); anything but an array
        // there (missing key, JSON null) means no hits.
        if (response.Data.ValueKind == JsonValueKind.Object
            && response.Data.TryGetProperty("Get", out var get)
            && get.ValueKind == JsonValueKind.Object
            && get.TryGetProperty(_options.ClassName, out var hits)
            && hits.ValueKind == JsonValueKind.Array)
        {
            return hits;
        }

        return default;
    }

    private static IReadOnlyList<SearchResult> MapResults(
        JsonElement hits,
        Func<JsonElement, double> scoreSelector,
        double minScore)
    {
        if (hits.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SearchResult>(hits.GetArrayLength());
        foreach (var hit in hits.EnumerateArray())
        {
            var score = scoreSelector(hit.GetProperty("_additional"));
            if (score < minScore)
                continue;

            var documentId = hit.GetProperty("document_id").GetString()!;
            var chunkIndex = hit.GetProperty("chunk_index").GetInt32();
            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = hit.GetProperty("text").GetString() ?? string.Empty,
                    DocumentId = new DocumentId(documentId),
                    ChunkIndex = chunkIndex,
                    Metadata = DeserializeMetadataOrThrow(hit, documentId, chunkIndex),
                },
                Score = score,
            });
        }

        return results;
    }

    /// <summary>
    /// Corrupt <c>metadata_json</c> is backend corruption — the store posture is to throw
    /// naming the object, never to silently return the chunk with its metadata dropped.
    /// </summary>
    private static Dictionary<string, MetadataValue> DeserializeMetadataOrThrow(
        JsonElement hit,
        string documentId,
        int chunkIndex)
    {
        var metadataResult = MetadataSerializer.DeserializeMetadata(
            hit.GetProperty("metadata_json").GetString());
        if (metadataResult.IsFailure)
        {
            throw new InvalidOperationException(
                $"Weaviate object {DeterministicObjectId(documentId, chunkIndex)} " +
                $"(document '{documentId}', chunk {chunkIndex}) has a corrupt metadata_json property.");
        }

        return metadataResult.Value;
    }

    /// <summary>Cosine distance (0 identical … 2 opposite) → similarity in [0, 1].</summary>
    private static double ScoreFromDistance(JsonElement additional) =>
        1.0 - (additional.GetProperty("distance").GetDouble() / 2.0);

    /// <summary>Hybrid fusion score in [0, 1]; Weaviate returns it as a JSON string.</summary>
    private static double ScoreFromHybridScore(JsonElement additional)
    {
        var score = additional.GetProperty("score");
        return score.ValueKind == JsonValueKind.String
            ? double.Parse(score.GetString()!, CultureInfo.InvariantCulture)
            : score.GetDouble();
    }

    private string BuildGetQuery(string searchArgument, SearchOptions options, string additionalField)
    {
        var sb = new StringBuilder(256);
        sb.Append("{ Get { ").Append(_options.ClassName).Append('(');
        if (_options.Tenant is { } tenant)
            sb.Append("tenant: ").Append(GraphQlString(tenant)).Append(", ");
        sb.Append(searchArgument);
        sb.Append(", limit: ").Append(options.TopK.ToString(CultureInfo.InvariantCulture));
        if (BuildWhereArgument(options.MetadataFilter) is { } where)
            sb.Append(", where: ").Append(where);
        sb.Append(") { text document_id chunk_index metadata_json _additional { ")
          .Append(additionalField)
          .Append(" } } } }");
        return sb.ToString();
    }

    /// <summary>
    /// Typed Equal conditions per filter pair: <c>valueNumber</c> for numbers,
    /// <c>valueBoolean</c> for booleans, <c>valueText</c> for strings and (sentinel-encoded)
    /// dates — matching exactly how <see cref="ToPropertyValue"/> stored them. Metadata stored
    /// before values carried types is all text, so non-string filters do not match it until
    /// re-ingested.
    /// </summary>
    private static string? BuildWhereArgument(IDictionary<string, MetadataValue>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        var sb = new StringBuilder(64);
        if (metadataFilter.Count > 1)
            sb.Append("{operator: And, operands: [");

        var first = true;
        foreach (var kvp in metadataFilter)
        {
            if (!first)
                sb.Append(", ");
            first = false;
            sb.Append("{path: [").Append(GraphQlString($"meta_{kvp.Key}"))
              .Append("], operator: Equal, ").Append(WhereValueArgument(kvp.Value))
              .Append('}');
        }

        if (metadataFilter.Count > 1)
            sb.Append("]}");
        return sb.ToString();
    }

    private static string WhereValueArgument(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.Number => "valueNumber: " + value.NumberValue.ToString(CultureInfo.InvariantCulture),
        MetadataValueKind.Boolean => "valueBoolean: " + (value.BooleanValue ? "true" : "false"),
        MetadataValueKind.DateTimeOffset => "valueText: " + GraphQlString(MetadataDateFormat.EncodeSentinel(value.DateTimeOffsetValue)),
        _ => "valueText: " + GraphQlString(value.StringValue),
    };

    /// <summary>
    /// Vector literal for an inlined GraphQL query (variables are unsupported — see
    /// <see cref="WeaviateGraphQlRequest"/>).
    /// </summary>
    private static string FormatVector(ReadOnlySpan<float> vector)
    {
        var sb = new StringBuilder(vector.Length * 12);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(vector[i].ToString(CultureInfo.InvariantCulture));
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Quoted, escaped GraphQL string literal. GraphQL string escaping is a subset of JSON
    /// string escaping, so JSON encoding produces a valid literal.
    /// </summary>
    private static string GraphQlString(string value) =>
        $"\"{JsonEncodedText.Encode(value)}\"";

    private static void ThrowOnBatchFailures(IReadOnlyList<WeaviateBatchObjectResult> items)
    {
        List<string>? failures = null;
        foreach (var item in items)
        {
            if (!string.Equals(item.Result?.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
                continue;

            failures ??= [];
            var errors = item.Result?.Errors?.Error;
            if (errors is { Count: > 0 })
            {
                foreach (var error in errors)
                    failures.Add(error.Message ?? "unknown error");
            }
            else
            {
                failures.Add("unknown error");
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Weaviate batch store failed for {failures.Count} object(s): {string.Join("; ", failures)}");
        }
    }

    private static T Unwrap<T>(Result<T, HttpError> result, string operation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Weaviate {operation} failed with {FormatHttpError(result.Error)}.");
        }

        return result.Value;
    }

    /// <summary>
    /// ZeroAlloc.Rest's failure path discards the response body, so <c>Error.Message</c> is
    /// frequently empty — omit the tail instead of ending the message in a dangling colon.
    /// </summary>
    private static string FormatHttpError(HttpError error) =>
        string.IsNullOrEmpty(error.Message)
            ? $"HTTP {(int)error.StatusCode}"
            : $"HTTP {(int)error.StatusCode}: {error.Message}";

    private static HttpClient CreateHttpClient(WeaviateOptions options)
    {
        // Same transient-fault posture as the DI-registered ZeroAlloc.Rest providers
        // (AddStandardResilienceHandler): retry on 5xx/408/timeouts. Safe for our POSTs —
        // deterministic object ids make every write idempotent.
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions())
            .Build();
        var handler = new ResilienceHandler(pipeline)
        {
            // Finite pooled-connection lifetime so this singleton's long-lived HttpClient
            // still observes DNS changes (the IHttpClientFactory rotation equivalent).
            InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
        };
        var client = new HttpClient(handler) { BaseAddress = options.Endpoint };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(options.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return client;
    }
}
