using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;
using RagSearchOptions = Rag.NET.Models.Options.SearchOptions;

namespace Rag.NET.AzureAISearch;

public sealed class AzureAISearchVectorStore : IVectorStore, IHybridSearchable, ICollectionManageable
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly string _indexName;
    private readonly int _vectorDimensions;

    public AzureAISearchVectorStore(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions = 1536)
        : this(endpoint, indexName, credential, vectorDimensions, clientOptions: null)
    {
    }

    public AzureAISearchVectorStore(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions,
        SearchClientOptions? clientOptions)
    {
        _indexClient = clientOptions is null
            ? new SearchIndexClient(endpoint, credential)
            : new SearchIndexClient(endpoint, credential, clientOptions);
        _searchClient = clientOptions is null
            ? new SearchClient(endpoint, indexName, credential)
            : new SearchClient(endpoint, indexName, credential, clientOptions);
        _indexName = indexName;
        _vectorDimensions = vectorDimensions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _indexClient.CreateOrUpdateIndexAsync(BuildIndex(_indexName, _vectorDimensions), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The index schema. Metadata lives in two fields:
    /// <list type="bullet">
    /// <item><description><c>metadata_entries</c> — a <c>Collection(Edm.ComplexType)</c> of
    /// <c>{key, stringValue, numberValue, boolValue, dateValue}</c> rows, one per metadata key,
    /// with the sub-field matching the value's <see cref="MetadataValueKind"/> populated. This is
    /// what makes every metadata key filterable with its type and no per-key schema:
    /// <c>metadata_entries/any(m: m/key eq 'page' and m/numberValue eq 3)</c>.</description></item>
    /// <item><description><c>metadata</c> — the legacy single-string JSON blob. Still declared
    /// and written because Azure AI Search forbids removing or re-typing an existing field:
    /// an index created before <c>metadata_entries</c> existed can only be updated
    /// <i>additively</i>, so the schema must keep carrying the old field for
    /// <c>CreateOrUpdateIndexAsync</c> to succeed against it. It doubles as the read fallback
    /// for documents ingested before the complex collection existed.</description></item>
    /// </list>
    /// Sub-fields of a complex collection cannot be marked <c>sortable</c> (service constraint:
    /// they are multi-valued per document), so none are.
    /// </summary>
    private static SearchIndex BuildIndex(string name, int vectorDimensions)
    {
        var metadataEntries = new ComplexField("metadata_entries", collection: true);
        metadataEntries.Fields.Add(new SimpleField("key", SearchFieldDataType.String) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("stringValue", SearchFieldDataType.String) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("numberValue", SearchFieldDataType.Double) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("boolValue", SearchFieldDataType.Boolean) { IsFilterable = true });
        metadataEntries.Fields.Add(new SimpleField("dateValue", SearchFieldDataType.DateTimeOffset) { IsFilterable = true });

        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunk_index", SearchFieldDataType.Int32),
            new SearchableField("text"),
            new SimpleField("metadata", SearchFieldDataType.String),
            metadataEntries,
            new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = vectorDimensions,
                VectorSearchProfileName = "default-profile",
            },
        };

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));

        return new SearchIndex(name)
        {
            Fields = fields,
            VectorSearch = vectorSearch,
        };
    }

    public async Task StoreAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.upsert");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);
        activity?.SetTag("vectorstore.batch.size", chunks.Count);

        var documents = chunks.Select(chunk => new SearchDocument(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["document_id"] = chunk.Chunk.DocumentId,
            ["chunk_index"] = chunk.Chunk.ChunkIndex,
            ["text"] = chunk.Chunk.Text,
            // Both metadata representations are written: the typed complex collection is what
            // filters run against; the JSON blob keeps documents readable by pre-complex-schema
            // readers and is the read fallback for pre-migration documents.
            ["metadata"] = MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata),
            ["metadata_entries"] = BuildMetadataEntries(chunk.Chunk.Metadata),
            ["embedding"] = chunk.Embedding.ToArray(),
        })).ToList();

        var batch = IndexDocumentsBatch.Upload(documents);
        await _searchClient.IndexDocumentsAsync(batch, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Azure AI Search indexing is near real-time; brief wait for consistency
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        RagSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);

        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = options.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = options.TopK,
                        Fields = { "embedding" },
                    },
                },
            },
        };

        searchOptions.Filter = BuildMetadataFilter(options.MetadataFilter);

        var results = await ExecuteSearchAsync(null, searchOptions, options.MinScore, cancellationToken)
            .ConfigureAwait(false);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        RagSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.search");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);
        activity?.SetTag("vectorstore.hybrid", true);

        var searchOptions = new Azure.Search.Documents.SearchOptions
        {
            Size = options.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = options.TopK,
                        Fields = { "embedding" },
                    },
                },
            },
            QueryType = SearchQueryType.Simple,
            SearchMode = SearchMode.Any,
        };

        searchOptions.Filter = BuildMetadataFilter(options.MetadataFilter);

        var results = await ExecuteSearchAsync(textQuery, searchOptions, options.MinScore, cancellationToken)
            .ConfigureAwait(false);
        activity?.SetTag("vectorstore.result.count", results.Count);
        return results;
    }

    public Task DeleteByDocumentIdAsync(
        string documentId,
        CancellationToken cancellationToken = default) =>
        DeleteByDocumentIdAsync(documentId, pageSize: 1000, cancellationToken);

    internal async Task DeleteByDocumentIdAsync(
        string documentId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (pageSize > 1000)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must not exceed 1000 (Azure AI Search maximum).");

        using var activity = RagTelemetrySource.ActivitySource.StartActivity("ragnet.vectorstore.delete");
        activity?.SetTag("vector.store", GetType().Name);
        activity?.SetTag("vectorstore.collection", _indexName);

        List<string> idsToDelete;
        do
        {
            idsToDelete = [];

            var searchOptions = new Azure.Search.Documents.SearchOptions
            {
                Filter = $"document_id eq '{EscapeODataString(documentId)}'",
                Select = { "id" },
                Size = pageSize,
            };

            var response = await _searchClient.SearchAsync<SearchDocument>(
                null, searchOptions, cancellationToken).ConfigureAwait(false);

            await foreach (var result in response.Value.GetResultsAsync()
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                idsToDelete.Add(result.Document.GetString("id"));
            }

            if (idsToDelete.Count > 0)
            {
                var batch = IndexDocumentsBatch.Delete("id", idsToDelete);
                await _searchClient.IndexDocumentsAsync(
                        batch,
                        new IndexDocumentsOptions { ThrowOnAnyError = true },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        } while (idsToDelete.Count > 0);
    }

    public async Task CreateCollectionAsync(string name, int vectorDimensions, CancellationToken cancellationToken = default)
    {
        await _indexClient.CreateOrUpdateIndexAsync(BuildIndex(name, vectorDimensions), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await _indexClient.DeleteIndexAsync(name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Delete-of-missing is a no-op (the ICollectionManageable contract). The service
            // answers 404 for an absent index, which the SDK raises. The local simulator used
            // by the tests returns success instead, so this guard is verified against the
            // documented service behaviour rather than by the container suite.
        }
    }

    public async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await _indexClient.GetIndexAsync(name, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Escapes a string for use in an OData string literal by doubling single quotes.</summary>
    internal static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// One <c>metadata_entries/any(...)</c> clause per filter pair, AND-composed. The clause
    /// compares the sub-field matching the filter value's <see cref="MetadataValueKind"/>, so a
    /// Number filter of <c>3</c> matches metadata stored as the number 3 and never the string
    /// <c>"3"</c>. Documents ingested before the complex collection existed have no
    /// <c>metadata_entries</c> rows and therefore do not match — re-ingest them to filter on them.
    /// </summary>
    internal static string? BuildMetadataFilter(IDictionary<string, MetadataValue>? metadataFilter)
    {
        if (metadataFilter is not { Count: > 0 })
            return null;

        var clauses = metadataFilter.Select(kvp =>
            "metadata_entries/any(m: m/key eq " +
            $"'{EscapeODataString(kvp.Key)}' and m/{SubFieldName(kvp.Value.Kind)} eq {FormatODataLiteral(kvp.Value)})");
        return string.Join(" and ", clauses);
    }

    private static string SubFieldName(MetadataValueKind kind) => kind switch
    {
        MetadataValueKind.Number => "numberValue",
        MetadataValueKind.Boolean => "boolValue",
        MetadataValueKind.DateTimeOffset => "dateValue",
        _ => "stringValue",
    };

    /// <summary>
    /// The filter value as an OData literal. Strings are quoted and escaped; numbers, booleans
    /// and dates use <see cref="MetadataValue.ToString"/>, whose invariant forms (<c>3</c>,
    /// <c>true</c>, <c>2026-01-01T00:00:00.0000000Z</c>) are already valid OData literals.
    /// </summary>
    private static string FormatODataLiteral(MetadataValue value) => value.Kind switch
    {
        MetadataValueKind.String => $"'{EscapeODataString(value.StringValue)}'",
        _ => value.ToString(),
    };

    /// <summary>One complex-collection row per metadata key, populating the sub-field for its kind.</summary>
    private static List<SearchDocument> BuildMetadataEntries(IDictionary<string, MetadataValue> metadata)
    {
        var entries = new List<SearchDocument>(metadata.Count);
        foreach (var (key, value) in metadata)
        {
            var entry = new SearchDocument { ["key"] = key };
            switch (value.Kind)
            {
                case MetadataValueKind.Number:
                    entry["numberValue"] = value.NumberValue;
                    break;
                case MetadataValueKind.Boolean:
                    entry["boolValue"] = value.BooleanValue;
                    break;
                case MetadataValueKind.DateTimeOffset:
                    entry["dateValue"] = value.DateTimeOffsetValue;
                    break;
                default:
                    entry["stringValue"] = value.StringValue;
                    break;
            }

            entries.Add(entry);
        }

        return entries;
    }

    private async Task<IReadOnlyList<SearchResult>> ExecuteSearchAsync(
        string? searchText,
        Azure.Search.Documents.SearchOptions searchOptions,
        double minScore,
        CancellationToken cancellationToken)
    {
        var response = await _searchClient.SearchAsync<SearchDocument>(
            searchText, searchOptions, cancellationToken).ConfigureAwait(false);

        var results = new List<SearchResult>();

        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var score = result.Score ?? 0.0;
            if (score < minScore)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    DocumentId = new DocumentId(result.Document.GetString("document_id")),
                    ChunkIndex = result.Document.GetInt32("chunk_index") ?? 0,
                    Text = result.Document.GetString("text"),
                    Metadata = ReadMetadata(result.Document),
                },
                Score = score,
            });
        }

        return results;
    }

    /// <summary>
    /// Metadata from the typed <c>metadata_entries</c> collection when the document has rows
    /// there; otherwise from the legacy <c>metadata</c> JSON blob — the path every document
    /// ingested before the complex collection existed takes, where all values read back as
    /// <see cref="MetadataValueKind.String"/> (exactly what was stored).
    /// </summary>
    private static Dictionary<string, MetadataValue> ReadMetadata(SearchDocument document)
    {
        if (TryReadMetadataEntries(document) is { } typed)
            return typed;

        var metadataResult = MetadataSerializer.DeserializeMetadata(document.GetString("metadata"));
        return metadataResult.IsSuccess
            ? metadataResult.Value
            : new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
    }

    private static Dictionary<string, MetadataValue>? TryReadMetadataEntries(SearchDocument document)
    {
        if (!document.TryGetValue("metadata_entries", out var raw) || raw is not IEnumerable<object> items)
            return null;

        Dictionary<string, MetadataValue>? metadata = null;
        foreach (var item in items)
        {
            metadata ??= new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            if (item is not SearchDocument entry || entry.GetString("key") is not { } key)
                continue;
            metadata[key] = ReadEntryValue(entry);
        }

        // No rows at all means "written before the complex collection existed" — signal the
        // caller to fall back to the blob. (A new document with empty metadata also lands here;
        // its blob is "{}", so both paths agree on the empty dictionary.)
        return metadata;
    }

    private static MetadataValue ReadEntryValue(SearchDocument entry)
    {
        // The SDK's dynamic deserializer maps a JSON number to int/long/double by shape, and
        // Edm.Double serialises whole numbers without a fractional part — so 3.0 written to
        // numberValue can come back as an int 3. Accept every numeric shape, not just double.
        if (entry.TryGetValue("numberValue", out var number))
        {
            switch (number)
            {
                case double d: return d;
                case float f: return (double)f;
                case int i: return (double)i;
                case long l: return (double)l;
            }
        }
        if (entry.TryGetValue("boolValue", out var boolean) && boolean is bool b)
            return b;
        // Edm.DateTimeOffset usually deserialises to DateTimeOffset, but a service (or the
        // local simulator) can hand the ISO-8601 text back as a plain string — same instant,
        // different shape. Parse it rather than letting a real date degrade to a string kind.
        if (entry.TryGetValue("dateValue", out var date))
        {
            if (date is DateTimeOffset dto)
                return dto;
            if (date is string dateText && DateTimeOffset.TryParse(
                    dateText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
                return parsed;
        }
        // The SDK's dynamic deserializer turns ISO-8601-looking strings into DateTimeOffset even
        // in stringValue; normalise such a value back to its textual form rather than dropping it.
        if (entry.TryGetValue("stringValue", out var text) && text is not null)
        {
            return text switch
            {
                string s => s,
                DateTimeOffset dtoText => MetadataDateFormat.Format(dtoText),
                _ => text.ToString() ?? string.Empty,
            };
        }

        return string.Empty;
    }
}
