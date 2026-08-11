using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Pipeline;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.SelfQuery;

namespace Rag.NET.DependencyInjection;

public sealed class RetrievalPipelineBuilder
{
    private readonly List<Type> _types =
    [
        typeof(SelfQueryBehavior),
        typeof(ResultCacheBehavior),
        typeof(LostInTheMiddleBehavior),
        // Inside LostInTheMiddle and outside everything that ranks, so the budget drops
        // from a settled ranking and the reorder then applies to the survivors. The other
        // way round drops whichever chunk ended up last, which after lost-in-the-middle is
        // a mid-ranked one.
        typeof(ContextBudgetBehavior),
        typeof(MmrBehavior),
        typeof(RedundancyFilterBehavior),
        typeof(ParentDocumentRetrievalBehavior),
        typeof(RerankingBehavior),
        typeof(RetrievalGuardBehavior),
        typeof(AdaptiveRetrievalBehavior),
        typeof(CorrectiveRagBehavior),
        typeof(MultiQueryBehavior),
        typeof(HydeBehavior),
        typeof(EmbeddingCacheBehavior),
        typeof(FilterBehavior),
        typeof(EnsembleBehavior),   // hybrid RRF; must run before VectorStoreBehavior
        typeof(VectorStoreBehavior),
    ];

    public RetrievalPipelineBuilder Add<T>(Type? after = null, Type? before = null)
        where T : IRetrievalBehavior
    {
        var raw = after is not null ? _types.IndexOf(after)
                : before is not null ? _types.IndexOf(before)
                : _types.Count;
        var idx = raw < 0 ? _types.Count
                : after is not null ? raw + 1
                : raw;
        _types.Insert(idx, typeof(T));
        return this;
    }

    /// <summary>
    /// Inserts <typeparamref name="T"/> at the start of the pipeline, making it the outermost
    /// (first-to-run) behavior. Use for cross-cutting concerns that should observe the final
    /// results returned to the caller after all inner behaviors have executed.
    /// </summary>
    public RetrievalPipelineBuilder AddFirst<T>()
        where T : IRetrievalBehavior
    {
        _types.Insert(0, typeof(T));
        return this;
    }

    public RetrievalPipelineBuilder Replace<TOld, TNew>()
        where TOld : IRetrievalBehavior
        where TNew : IRetrievalBehavior
    {
        var idx = _types.IndexOf(typeof(TOld));
        if (idx >= 0) _types[idx] = typeof(TNew);
        return this;
    }

    internal IReadOnlyList<Type> GetBehaviorTypes() => _types.AsReadOnly();

    public Pipeline<RetrievalContext, IReadOnlyList<SearchResult>> Build(IServiceProvider sp)
    {
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> chain =
            static (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([]);

        for (var i = _types.Count - 1; i >= 0; i--)
        {
            var behavior = (IRetrievalBehavior)sp.GetRequiredService(_types[i]);
            var next = chain;
            chain = (ctx, ct) => behavior.HandleAsync(ctx, ct, next);
        }

        return new Pipeline<RetrievalContext, IReadOnlyList<SearchResult>>(chain);
    }
}
