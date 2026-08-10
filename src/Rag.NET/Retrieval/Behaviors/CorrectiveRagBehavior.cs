using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class CorrectiveRagBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }
    [Inject(Required = false)] public IWebSearch? WebSearch { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseCrag || WebSearch is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var results = await next(ctx, ct).ConfigureAwait(false);
        var score = await ScoreRelevanceAsync(ctx, results, ct).ConfigureAwait(false);

        if (score >= ctx.Options.CragScoreThreshold)
        {
            ctx.Extensions["crag_triggered"] = "false";
            return results;
        }

        ctx.Extensions["crag_triggered"] = "true";

        try
        {
            var webResults = await WebSearch.SearchAsync(ctx.Query, ctx.Options.TopK, ct).ConfigureAwait(false);
            return ctx.Options.CragFallbackMode == CragFallbackMode.Append
                ? [.. results, .. webResults]
                : webResults;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.CragWebSearchFailed(ctx.Logger, ctx.Query, ex);
            return results;
        }
    }

    private async Task<float> ScoreRelevanceAsync(
        RetrievalContext ctx,
        IReadOnlyList<SearchResult> results,
        CancellationToken ct)
    {
        if (results.Count == 0) return 0f;

        if (ChatClient is not null)
        {
            try
            {
                return await ScoreWithLlmAsync(ctx.Query, results, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RagPipelineLog.CragLlmScoringFailed(ctx.Logger, ctx.Query, ex);
            }
        }

        return ScoreWithHeuristic(ctx.Query, results);
    }

    private async Task<float> ScoreWithLlmAsync(
        string query,
        IReadOnlyList<SearchResult> results,
        CancellationToken ct)
    {
        var relevant = 0;
        foreach (var result in results)
        {
            var response = await ChatClient!.GetResponseAsync(
                [new ChatMessage(ChatRole.User, $"""
                    Is this chunk relevant to the query?
                    Query: {query}
                    Chunk: {result.Chunk.Text}
                    Reply with exactly one word: relevant, ambiguous, or irrelevant.
                    """)],
                cancellationToken: ct).ConfigureAwait(false);

            var label = response.Text?.Trim().ToLowerInvariant() ?? "irrelevant";
            if (string.Equals(label, "relevant", StringComparison.Ordinal)) relevant++;
        }
        return (float)relevant / results.Count;
    }

    internal static float ScoreWithHeuristic(string query, IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0) return 0f;

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return 0f;

        var matchingResults = 0;
        foreach (var result in results)
        {
            var chunkTokens = Tokenize(result.Chunk.Text);
            var matched = 0;
            foreach (var token in queryTokens)
            {
                if (chunkTokens.Contains(token))
                    matched++;
            }
            if ((float)matched / queryTokens.Count >= 0.3f)
                matchingResults++;
        }
        return (float)matchingResults / results.Count;
    }

    // Ordinal stated rather than defaulted (MA0002). A collection expression cannot carry a
    // comparer. Casing is already settled by ToLowerInvariant below, so the set never had to
    // fold case itself and Ordinal preserves the previous behaviour exactly.
    private static HashSet<string> Tokenize(string text) =>
        new(
            text.Split([' ', '.', ',', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant()),
            StringComparer.Ordinal);
}
