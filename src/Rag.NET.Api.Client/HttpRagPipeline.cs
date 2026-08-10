using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Abstractions;
using Rag.NET.Api.Contracts;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Api.Client;

public sealed class HttpRagPipeline : IRagPipeline
{
    private readonly HttpClient _httpClient;

    public HttpRagPipeline(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(document, Encoding.UTF8, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var request = new IngestRequest
        {
            Content = content,
            DocumentId = metadata.DocumentId,
            FileName = metadata.FileName,
            ContentType = metadata.ContentType,
            // The REST wire contract still carries tags as strings (ToString is lossless as
            // text but drops the kind); a typed JSON contract is follow-up work tracked with
            // the typed-metadata change.
            Tags = metadata.Tags.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal)
        };

        var response = await _httpClient.PostAsJsonAsync("/rag/ingest", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var ingestResponse = await response.Content.ReadFromJsonAsync<IngestResponse>(cancellationToken).ConfigureAwait(false);
        return Result<IngestionResult, RagError>.Success(new IngestionResult
        {
            DocumentId = new DocumentId(ingestResponse!.DocumentId),
            ChunksStored = ingestResponse.ChunksStored
        });
    }

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new RetrieveRequest
        {
            Query = query,
            TopK = options?.TopK ?? 5,
            UseHybridSearch = options?.UseHybridSearch ?? true
        };

        var response = await _httpClient.PostAsJsonAsync("/rag/retrieve", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var retrieveResponse = await response.Content.ReadFromJsonAsync<RetrieveResponse>(cancellationToken).ConfigureAwait(false);
        return Result<IReadOnlyList<SearchResult>, RagError>.Success(
            retrieveResponse!.Results.Select(MapToSearchResult).ToList());
    }

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = new AskRequest
        {
            Query = query,
            TopK = options?.TopK ?? 5,
            UseHybridSearch = options?.UseHybridSearch ?? true
        };

        var response = await _httpClient.PostAsJsonAsync("/rag/ask", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var askResponse = await response.Content.ReadFromJsonAsync<AskResponse>(cancellationToken).ConfigureAwait(false);
        return new RagResponse
        {
            Answer = askResponse!.Answer,
            Sources = askResponse.Sources.Select(MapToSearchResult).ToList()
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var encodedQuery = Uri.EscapeDataString(query);
        using var response = await _httpClient.GetAsync(
            $"/rag/ask/stream?query={encodedQuery}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var text = line["data: ".Length..];
                yield return new RagStreamingUpdate { TextDelta = text };
            }
        }
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.DeleteAsync($"/rag/documents/{documentId}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static SearchResult MapToSearchResult(SearchResultDto dto) =>
        new()
        {
            Score = dto.Score,
            Chunk = new TextChunk
            {
                Text = dto.Text,
                DocumentId = new DocumentId(dto.DocumentId),
                ChunkIndex = dto.ChunkIndex,
                // The REST wire format carries metadata as strings, so everything read through
                // the HTTP client is String-kind — same as data stored before values had types.
                Metadata = dto.Metadata.ToDictionary(kv => kv.Key, kv => (MetadataValue)kv.Value, StringComparer.Ordinal)
            }
        };
}
