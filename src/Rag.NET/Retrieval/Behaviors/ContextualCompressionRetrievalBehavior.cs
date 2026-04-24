using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;

namespace Rag.NET.Retrieval.Behaviors;

/// <summary>
/// Retrieval-pipeline wrapper around <see cref="IContextualCompressor"/> — runs
/// compression on the pipeline output so plain <c>RetrieveAsync</c> callers see
/// compressed text. Opt-in via <c>UseContextualCompressionInRetrieval()</c>.
/// </summary>
public sealed class ContextualCompressionRetrievalBehavior(IContextualCompressor compressor) : IRetrievalBehavior
{
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx,
        CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);
        return await compressor.CompressAsync(results, ctx.Query, ct).ConfigureAwait(false);
    }
}
