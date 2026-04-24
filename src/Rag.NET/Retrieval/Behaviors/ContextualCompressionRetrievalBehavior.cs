using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

/// <summary>
/// Retrieval-pipeline wrapper around <see cref="IContextualCompressor"/> — runs
/// compression on the pipeline output so plain <c>RetrieveAsync</c> callers see
/// compressed text. Opt-in via <c>UseContextualCompressionInRetrieval()</c>.
/// </summary>
[Singleton]
public sealed class ContextualCompressionRetrievalBehavior : IRetrievalBehavior
{
    [Inject] public IContextualCompressor Compressor { get; set; } = null!;

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx,
        CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        // ctx.Query reflects any upstream transformation (HyDE/MultiQuery); compressing
        // against the same query the retrieval ran on is the correct relevance signal.
        try
        {
            return await Compressor.CompressAsync(results, ctx.Query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.ContextualCompressionFailed(ctx.Logger, ctx.Query, ex);
            return results;
        }
    }
}
