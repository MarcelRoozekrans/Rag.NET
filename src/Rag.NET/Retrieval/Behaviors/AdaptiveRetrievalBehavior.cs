using Microsoft.Extensions.AI;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class AdaptiveRetrievalBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseAdaptiveRetrieval)
            return await next(ctx, ct).ConfigureAwait(false);

        var complexity = ClassifyHeuristic(ctx.Query);

        if (complexity is null && ChatClient is not null)
        {
            try
            {
                complexity = await ClassifyWithLlmAsync(ctx.Query, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RagPipelineLog.AdaptiveClassificationFailed(ctx.Logger, ctx.Query, ex);
            }
        }

        complexity ??= "complex";

        var options = complexity switch
        {
            "simple"    => ctx.Options with { TopK = 3,  UseMultiQuery = false, UseHyde = false },
            "multi_hop" => ctx.Options with { TopK = 10, UseMultiQuery = true,  UseHyde = true  },
            _           => ctx.Options with { TopK = 8,  UseMultiQuery = true,  UseHyde = false },
        };

        ctx.Extensions["adaptive_complexity"] = complexity;

        return await next(ctx with { Options = options }, ct).ConfigureAwait(false);
    }

    internal static string? ClassifyHeuristic(string query)
    {
        var lower = " " + query.ToLowerInvariant() + " ";

        var multiHopKeywords = new[] { " and ", " also ", " additionally ", " furthermore ", " as well as " };
        var conjunctionCount = multiHopKeywords.Sum(k =>
            CountOccurrences(lower, k, StringComparison.Ordinal));
        if (conjunctionCount >= 2)
            return "multi_hop";

        var complexKeywords = new[] { "how", "why", "compare", "difference", "explain" };
        if (complexKeywords.Any(k => query.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return "complex";

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 6)
            return "simple";

        return null;
    }

    private static int CountOccurrences(string text, string pattern, StringComparison comparison)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, comparison)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private async Task<string> ClassifyWithLlmAsync(string query, CancellationToken ct)
    {
        var response = await ChatClient!.GetResponseAsync(
            [new ChatMessage(ChatRole.User, $"""
                Classify this query as exactly one of: simple, complex, multi_hop.

                simple = single-concept lookup, short, no comparison
                complex = multi-aspect explanation, comparison, or analysis
                multi_hop = requires connecting 2+ separate concepts or sources

                Query: {query}

                Reply with ONLY the classification word.
                """)],
            cancellationToken: ct).ConfigureAwait(false);

        var text = response.Text?.Trim().ToLowerInvariant() ?? "complex";
        return text is "simple" or "complex" or "multi_hop" ? text : "complex";
    }
}
