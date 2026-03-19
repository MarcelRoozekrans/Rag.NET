using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class MultiQueryBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IQueryExpander? QueryExpander { get; set; }
    [Inject(Required = false)] public MultiQueryOptions? MultiQueryOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseMultiQuery || QueryExpander is null)
            return await next(ctx, ct).ConfigureAwait(false);

        IReadOnlyList<string> variants;
        try
        {
            var variantCount = MultiQueryOptions?.VariantCount ?? new MultiQueryOptions().VariantCount;
            variants = await QueryExpander.ExpandAsync(ctx.Query, variantCount, ct).ConfigureAwait(false);
            RagPipelineLog.QueryExpansionCompleted(ctx.Logger, ctx.Query, variants.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.QueryExpansionFailed(ctx.Logger, ctx.Query, ex);
            variants = [];
        }

        var allQueries = new List<string>(variants.Count + 1) { ctx.Query };
        allQueries.AddRange(variants);

        var tasks = allQueries.Select(q => SafeRetrieveAsync(q, ctx, ct, next)).ToArray();
        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        return allResults
            .Where(r => r is not null)
            .SelectMany(r => r!)
            .GroupBy(r => (r.Chunk.DocumentId, r.Chunk.ChunkIndex))
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(ctx.Options.TopK)
            .ToList()
            .AsReadOnly();
    }

    private static async Task<IReadOnlyList<SearchResult>?> SafeRetrieveAsync(
        string query, RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        try
        {
            return await next(ctx with { Query = query, Options = ctx.Options with { UseMultiQuery = false } }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.QueryRetrievalFailed(ctx.Logger, query, ex);
            return null;
        }
    }
}
