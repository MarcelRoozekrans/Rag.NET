using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.WebSearch.Tavily;

internal sealed class TavilyWebSearch : IWebSearch
{
    private readonly ITavilyApi _api;

    /// <summary>Creates the provider over a configured API client.</summary>
    /// <param name="api">The client, whose HttpClient carries the Authorization header.</param>
    /// <remarks>
    /// <b>No API key parameter, since #625.</b> The credential belongs on the transport, not in
    /// the payload: it is set once as an Authorization: Bearer header by
    /// <see cref="TavilyWebSearchExtensions.AddTavilyWebSearch"/>. Taking a key here again would
    /// invite putting it back in the body, which is the deprecated form Tavily rejects.
    /// </remarks>
    public TavilyWebSearch(ITavilyApi api)
    {
        _api = api;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        var request = new TavilySearchRequest { Query = query, MaxResults = topK };
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
