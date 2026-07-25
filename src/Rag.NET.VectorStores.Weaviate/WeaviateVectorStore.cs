using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
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
    private bool _initialized;

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
        var query = BuildGetQuery(
            $"nearVector: {{vector: {FormatVector(queryEmbedding.Span)}}}",
            options,
            additionalField: "distance");
        var hits = await ExecuteGetQueryAsync(query, cancellationToken).ConfigureAwait(false);
        return MapResults(hits, ScoreFromDistance, options.MinScore);
    }

    public async Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var query = BuildGetQuery(
            $"hybrid: {{query: {GraphQlString(textQuery)}, vector: {FormatVector(queryEmbedding.Span)}}}",
            options,
            additionalField: "score");
        var hits = await ExecuteGetQueryAsync(query, cancellationToken).ConfigureAwait(false);
        return MapResults(hits, ScoreFromHybridScore, options.MinScore);
    }

    public async Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        var result = await _api.BatchDeleteAsync(
            new WeaviateBatchDeleteRequest
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
            },
            _options.Tenant,
            cancellationToken).ConfigureAwait(false);
        Unwrap(result, "batch delete");
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
            $"Weaviate class probe for '{name}' failed with HTTP {(int)result.Error.StatusCode}: {result.Error.Message}");
    }

    public void Dispose() => _ownedHttpClient?.Dispose();

    /// <summary>
    /// Deterministic object id per <c>(DocumentId, ChunkIndex)</c> (SHA-256 truncated to a
    /// GUID with version/variant bits set — the <c>QdrantSparseVectorStore</c> derivation)
    /// so re-storing a chunk replaces its object instead of duplicating it.
    /// PERSISTENT STORAGE CONTRACT: existing classes hold objects with these ids — the
    /// algorithm must never change.
    /// </summary>
    internal static Guid DeterministicObjectId(string documentId, int chunkIndex)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}\n{chunkIndex}"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x80); // version 8 (custom, RFC 9562)
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(guidBytes);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
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

        // Metadata keys become meta_-prefixed text properties via Weaviate auto-schema
        // (enabled by default — verified: new properties are accepted without any env
        // toggle), making them server-side filterable like Qdrant's meta_ payload fields.
        foreach (var kvp in chunk.Metadata)
            properties[$"meta_{kvp.Key}"] = kvp.Value;

        return properties;
    }

    private async Task<JsonElement> ExecuteGetQueryAsync(string query, CancellationToken cancellationToken)
    {
        var result = await _api.QueryAsync(new WeaviateGraphQlRequest(query), cancellationToken)
            .ConfigureAwait(false);
        var response = Unwrap(result, "GraphQL query");

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

        var results = new List<SearchResult>();
        foreach (var hit in hits.EnumerateArray())
        {
            var score = scoreSelector(hit.GetProperty("_additional"));
            if (score < minScore)
                continue;

            var metadataResult = MetadataSerializer.DeserializeMetadata(
                hit.GetProperty("metadata_json").GetString());
            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = hit.GetProperty("text").GetString() ?? string.Empty,
                    DocumentId = new DocumentId(hit.GetProperty("document_id").GetString()!),
                    ChunkIndex = hit.GetProperty("chunk_index").GetInt32(),
                    Metadata = metadataResult.IsSuccess
                        ? metadataResult.Value
                        : new Dictionary<string, string>(StringComparer.Ordinal),
                },
                Score = score,
            });
        }

        return results;
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

    private static string? BuildWhereArgument(IDictionary<string, string>? metadataFilter)
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
              .Append("], operator: Equal, valueText: ").Append(GraphQlString(kvp.Value))
              .Append('}');
        }

        if (metadataFilter.Count > 1)
            sb.Append("]}");
        return sb.ToString();
    }

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
                $"Weaviate {operation} failed with HTTP {(int)result.Error.StatusCode}: {result.Error.Message}");
        }

        return result.Value;
    }

    private static HttpClient CreateHttpClient(WeaviateOptions options)
    {
        // Same transient-fault posture as the DI-registered ZeroAlloc.Rest providers
        // (AddStandardResilienceHandler): retry on 5xx/408/timeouts. Safe for our POSTs —
        // deterministic object ids make every write idempotent.
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions())
            .Build();
        var handler = new ResilienceHandler(pipeline) { InnerHandler = new SocketsHttpHandler() };
        var client = new HttpClient(handler) { BaseAddress = options.Endpoint };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(options.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return client;
    }
}
