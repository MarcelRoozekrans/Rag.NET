using Cohere;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Telemetry;

namespace Rag.NET.Reranking.Cohere;

/// <summary>
/// Reranks search results using the Cohere Rerank API.
/// </summary>
public sealed class CohereReranker : IReranker, IDisposable
{
    private readonly CohereClient _client;
    private readonly HttpClient? _httpClient;
    private readonly CohereRerankerOptions _options;

    public CohereReranker(CohereRerankerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("ApiKey must not be null or whitespace.", nameof(options));

        _options = options;
        if (options.Endpoint is { } endpoint)
        {
            _httpClient = new HttpClient();
            _client = new CohereClient(options.ApiKey, _httpClient, new Uri(endpoint));
        }
        else
        {
            _client = new CohereClient(options.ApiKey);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Cohere caps individual document text at approximately 10,000 tokens.
    /// If a passage exceeds this limit, the Cohere SDK will throw. Chunk aggressively before reranking.
    /// </remarks>
    public async Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);

        // No span here: RerankerInstrumented opens ragnet.rerank.CohereReranker around this call
        // and tags it from the parameter and the return value.
        if (results.Count == 0)
            return [];

        var allRerankResults = new List<RerankResult>(results.Count);

        // Batch documents to respect Cohere's per-call limit
        for (var offset = 0; offset < results.Count; offset += _options.MaxDocumentsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(offset + _options.MaxDocumentsPerBatch, results.Count);
            var batchSize = batchEnd - offset;

            var documents = new List<OneOf<string, RerankDocument>>(batchSize);
            for (var i = offset; i < batchEnd; i++)
                documents.Add(results[i].Chunk.Text);

            var request = new RerankRequest
            {
                Query = query,
                Documents = documents,
                Model = _options.Model,
                TopN = _options.TopN,
                ReturnDocuments = _options.ReturnDocuments,
            };

            var response = await _client.RerankAsync(request, xClientName: "", cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var result in response.Results)
            {
                allRerankResults.Add(new RerankResult
                {
                    SearchResult = results[offset + result.Index],
                    RelevanceScore = result.RelevanceScore,
                });
            }
        }

        // Sort descending by score (Cohere returns pre-sorted per batch; re-sort after merge)
        allRerankResults.Sort(static (a, b) => b.RelevanceScore.CompareTo(a.RelevanceScore));
        if (allRerankResults.Count > _options.TopN)
            allRerankResults.RemoveRange(_options.TopN, allRerankResults.Count - _options.TopN);
        return allRerankResults;
    }

    public void Dispose()
    {
        _client.Dispose();
        _httpClient?.Dispose();
    }
}
