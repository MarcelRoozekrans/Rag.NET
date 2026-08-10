using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.WebSearch.Tavily;

internal sealed class TavilyWebSearch : IWebSearch
{
    private readonly ITavilyApi _api;
    private readonly string _apiKey;

    public TavilyWebSearch(ITavilyApi api, string apiKey)
    {
        _api = api;
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        var request = new TavilySearchRequest { ApiKey = _apiKey, Query = query, MaxResults = topK };
        var result = await _api.SearchAsync(body: request, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw new HttpRequestException($"Tavily search failed: {result.Error.StatusCode}");

        return result.Value.Results
            .Select(r => new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = r.Content,
                    DocumentId = new DocumentId(r.Url),
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["title"] = r.Title,
                        ["url"] = r.Url,
                        ["source"] = "tavily"
                    }
                },
                Score = r.Score
            })
            .ToList();
    }
}
