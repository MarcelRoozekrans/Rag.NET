using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZeroAlloc.Results;

namespace Rag.NET.Retrieval;

public sealed class DeepResearchRetriever : IRetriever
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Used only to read <see cref="RetrievalOptions.TopK"/>'s documented default when the caller
    /// passes no options. Reading it from the type rather than repeating <c>5</c> here means the
    /// contract has one definition, not two that can drift.
    /// </summary>
    private static readonly RetrievalOptions _defaultOptions = new();

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

        var topK = (options ?? _defaultOptions).TopK;

        // Two collections with two jobs, deliberately not one.
        //
        // `chunks` is the accumulated union and exists ONLY to be shown to the sufficiency check --
        // the model has to see everything gathered so far to judge whether more is needed, so it
        // must not be truncated. `rankings` keeps each retrieval's page intact and in its own rank
        // order, because that is the only comparable thing about them (see the fusion below).
        var chunks = result.Value.ToList();
        var rankings = new List<(IReadOnlyList<SearchResult> Hits, double Weight)>
        {
            (result.Value, 1.0),
        };

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
                    {
                        chunks.AddRange(sub.Value);
                        rankings.Add((sub.Value, 1.0));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Sub-query retrieval failed for '{SubQuery}'; skipping and continuing", subQuery);
                }
            }

            chunks = Deduplicate(chunks);
        }

        return Result<IReadOnlyList<SearchResult>, RagError>.Success(
            BuildPage(result.Value, rankings, topK));
    }

    /// <summary>
    /// Turns the retrievals gathered above into the page the caller asked for: at most
    /// <paramref name="topK"/> results (#475), ordered by fusing the rankings rather than by
    /// comparing their scores.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Truncating the old score-sorted union would have been the smaller change and the wrong
    /// one.</b> A sub-query's scores come from a different query vector than the caller's, so a
    /// chunk scoring 0.9 against <i>"what did X cost"</i> outranks one scoring 0.8 against the
    /// question actually asked. While nothing truncated, that only mis-<i>ordered</i> the page.
    /// Cutting it at <c>TopK</c> on that same ordering would have promoted it to deciding
    /// <i>content</i> — the caller's best-matching chunks dropped in favour of a sub-query's
    /// inflated ones. Fixing the contract that way trades a documented over-fetch for a silent
    /// quality loss.
    /// </para>
    /// <para>
    /// Reciprocal Rank Fusion reads only each hit's <b>position</b> in its own ranking, which is
    /// the one thing comparable across query vectors, and is what <c>EnsembleBehavior</c>'s
    /// client-side hybrid path already applies at this same boundary. A chunk several sub-queries
    /// independently rank highly can therefore outrank the primary query's top hit — consensus
    /// beating a single opinion, which is the point of having decomposed the question at all.
    /// </para>
    /// <para>
    /// <b>The no-expansion case stays a pass-through.</b> When the model was satisfied on the first
    /// pass — or every sub-query failed — there is one ranking, and fusing it with nothing would be
    /// arithmetically harmless but would still rewrite every <see cref="SearchResult.Score"/> onto
    /// the RRF scale for a call that added no information. The inner page is returned with its
    /// scores untouched, trimmed only if the inner retriever itself overshot.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SearchResult> BuildPage(
        IReadOnlyList<SearchResult> primary,
        List<(IReadOnlyList<SearchResult> Hits, double Weight)> rankings,
        int topK)
    {
        if (rankings.Count == 1)
            return primary.Count <= topK ? primary : [.. primary.Take(topK)];

        return RrfMerger.MergeMany(rankings, topK, RrfMerger.DefaultK);
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
