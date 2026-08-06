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
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunk_index", SearchFieldDataType.Int32),
            new SearchableField("text"),
            new SimpleField("metadata", SearchFieldDataType.String),
            new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = _vectorDimensions,
                VectorSearchProfileName = "default-profile",
            },
        };

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));

        var index = new SearchIndex(_indexName)
        {
            Fields = fields,
            VectorSearch = vectorSearch,
        };

        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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
            ["metadata"] = MetadataSerializer.SerializeMetadata(chunk.Chunk.Metadata),
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

        if (options.MetadataFilter is { Count: > 0 })
        {
            var filterClauses = options.MetadataFilter
                .Select(kvp =>
                    $"search.ismatch('\"{EscapeODataString(kvp.Key)}\":\"{EscapeODataString(kvp.Value)}\"', 'metadata')")
                .ToList();
            searchOptions.Filter = string.Join(" and ", filterClauses);
        }

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

        if (options.MetadataFilter is { Count: > 0 })
        {
            var filterClauses = options.MetadataFilter
                .Select(kvp =>
                    $"search.ismatch('\"{EscapeODataString(kvp.Key)}\":\"{EscapeODataString(kvp.Value)}\"', 'metadata')")
                .ToList();
            searchOptions.Filter = string.Join(" and ", filterClauses);
        }

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
        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunk_index", SearchFieldDataType.Int32),
            new SearchableField("text"),
            new SimpleField("metadata", SearchFieldDataType.String),
            new SearchField("embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = vectorDimensions,
                VectorSearchProfileName = "default-profile",
            },
        };

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));

        var index = new SearchIndex(name)
        {
            Fields = fields,
            VectorSearch = vectorSearch,
        };

        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken)
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

            var metadataJson = result.Document.GetString("metadata");
            var metadataResult = MetadataSerializer.DeserializeMetadata(metadataJson);
            var metadata = metadataResult.IsSuccess
                ? metadataResult.Value
                : new Dictionary<string, string>(StringComparer.Ordinal);

            results.Add(new SearchResult
            {
                Chunk = new TextChunk
                {
                    DocumentId = new DocumentId(result.Document.GetString("document_id")),
                    ChunkIndex = result.Document.GetInt32("chunk_index") ?? 0,
                    Text = result.Document.GetString("text"),
                    Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                },
                Score = score,
            });
        }

        return results;
    }
}
