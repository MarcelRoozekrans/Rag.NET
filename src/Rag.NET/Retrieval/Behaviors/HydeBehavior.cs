using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class HydeBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IHypotheticalDocumentGenerator? HydeGenerator { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseHyde || HydeGenerator is null)
            return await next(ctx, ct).ConfigureAwait(false);

        try
        {
            var doc = await HydeGenerator.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
            RagPipelineLog.HydeDocumentGenerated(ctx.Logger, ctx.Query, doc.Length);
            return await next(
                ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = doc } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.HydeGenerationFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx with { Options = ctx.Options with { UseHyde = false } }, ct).ConfigureAwait(false);
        }
    }
}
