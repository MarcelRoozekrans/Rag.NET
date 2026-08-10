using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Retrieval;

public sealed class DeepResearchRetriever : IRetriever
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRetriever _inner;
    private readonly IChatClient _chatClient;
    private readonly DeepResearchOptions _options;
    private readonly ILogger<DeepResearchRetriever>? _logger;

    public DeepResearchRetriever(
        IRetriever inner,
        IChatClient chatClient,
        DeepResearchOptions options,
        ILogger<DeepResearchRetriever>? logger = null)
    {
        _inner = inner;
        _chatClient = chatClient;
        _options = options;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return result;

        var chunks = result.Value.ToList();

        for (int depth = 0; depth < _options.MaxDepth; depth++)
        {
            var sufficiency = await CheckSufficiencyAsync(query, chunks, cancellationToken).ConfigureAwait(false);
            if (sufficiency.Sufficient)
                break;

            var raw = sufficiency.SubQueries;
            int subCount = Math.Min(raw.Length, _options.SubQueryCount);
            string[] subQueries = subCount == raw.Length ? raw : raw[..subCount];
            foreach (var subQuery in subQueries)
            {
                try
                {
                    var sub = await _inner.RetrieveAsync(subQuery, options, cancellationToken).ConfigureAwait(false);
                    if (sub.IsSuccess)
                        chunks.AddRange(sub.Value);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Sub-query retrieval failed for '{SubQuery}'; skipping and continuing", subQuery);
                }
            }

            chunks = Deduplicate(chunks);
        }

        return Result<IReadOnlyList<SearchResult>, RagError>.Success(chunks.AsReadOnly());
    }

    private static List<SearchResult> Deduplicate(List<SearchResult> chunks)
    {
        var seen = new Dictionary<(string, int), SearchResult>();
        foreach (ref readonly var r in CollectionsMarshal.AsSpan(chunks))
        {
            var key = (r.Chunk.DocumentId.Value, r.Chunk.ChunkIndex);
            if (!seen.TryGetValue(key, out var existing) || r.Score > existing.Score)
                seen[key] = r;
        }
        return [.. seen.Values.OrderByDescending(r => r.Score)];
    }

    private sealed record SufficiencyResponse(bool Sufficient, string[] SubQueries);

    private async Task<SufficiencyResponse> CheckSufficiencyAsync(
        string query, IList<SearchResult> chunks, CancellationToken cancellationToken)
    {
        var promptText = _options.SufficiencyPrompt ?? BuildDefaultPrompt(query, chunks);
        try
        {
            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, promptText)],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                cancellationToken).ConfigureAwait(false);

            // ResponseFormat = Json above is a request, not a guarantee — providers that ignore
            // it return fenced or preambled JSON, which used to fail below on every call.
            var json = LlmJsonExtractor.Extract(response.Text ?? "{}", LlmJsonPayloadKind.Object);
            return JsonSerializer.Deserialize<SufficiencyResponse>(json, _jsonOptions)
                   ?? new SufficiencyResponse(true, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex)
        {
            // Fail-open on purpose: deep research is an enhancement over the inner retriever's
            // results, which are already in hand. But an unreadable verdict on every call means
            // the feature is silently off, so it must at least be visible in the logs.
            _logger?.LogWarning(ex, "Sufficiency check response was not readable JSON; treating as sufficient.");
            return new SufficiencyResponse(true, []);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Sufficiency check failed; treating as sufficient.");
            return new SufficiencyResponse(true, []);
        }
    }

    private string BuildDefaultPrompt(string query, IList<SearchResult> chunks)
    {
        var context = string.Join("\n", chunks.Select(r => $"- {r.Chunk.Text}"));
        return $$"""
            Query: {{query}}
            Retrieved context:
            {{context}}

            Is the above context sufficient to answer the query? If not, provide up to {{_options.SubQueryCount}} focused sub-queries.
            Respond with JSON only: {"sufficient": true, "subQueries": []}
            """;
    }
}
