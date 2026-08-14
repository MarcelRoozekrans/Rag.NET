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

    /// <summary>
    /// Inserts <typeparamref name="T"/> into the chain, after or before an anchor type.
    /// </summary>
    /// <typeparam name="T">The behaviour to insert.</typeparam>
    /// <param name="after">Insert directly after this type.</param>
    /// <param name="before">Insert directly before this type. Ignored when <paramref name="after"/> is set.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// Idempotent, and for the same reason <see cref="AddFirst{T}"/> is: a behaviour reached from
    /// two directions would be inserted twice, and <see cref="Build"/> resolves the same singleton
    /// for both slots, so it would run twice per query with nothing about the container looking
    /// wrong. Two directions is now the normal case — a <c>Use*</c> method places its own
    /// behaviour, and the caller may also name it in <c>AddRagNet</c>'s <c>retrieval:</c>
    /// delegate, as <c>docs/guide/raptor.md</c> and <c>docs/guide/graphrag.md</c> teach.
    /// <b>The first insertion wins the position</b>, and those delegates run before
    /// <c>configure</c> does, so an explicit placement beats the <c>Use*</c> default rather than
    /// being silently overridden by it.
    /// </remarks>
    public RetrievalPipelineBuilder Add<T>(Type? after = null, Type? before = null)
        where T : IRetrievalBehavior
    {
        if (_types.Contains(typeof(T)))
        {
            return this;
        }

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
    /// <remarks>
    /// Idempotent: a second call for a behavior already in the pipeline leaves it where it is.
    /// The callers are registration extensions — <c>UseAuditLog</c>, <c>AddRagDiagnostics</c> —
    /// and layered composition roots reach those more than once. Inserting twice would run the
    /// observer twice per retrieval, so every query would be audited twice and traced twice,
    /// with nothing about the container looking wrong. Guarding here rather than at each call
    /// site covers callers outside this assembly, which cannot see the behavior list.
    /// </remarks>
    public RetrievalPipelineBuilder AddFirst<T>()
        where T : IRetrievalBehavior
    {
        if (_types.Contains(typeof(T)))
        {
            return this;
        }

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

    /// <summary>
    /// The behaviour types composing the pipeline, outermost first — the exact list
    /// <see cref="Build"/> resolves and chains.
    /// </summary>
    /// <returns>The ordered chain, as a read-only view over the live list.</returns>
    /// <remarks>
    /// Public because a <c>Use*</c> extension in another package has to be able to see the chain
    /// it is inserting into, and a test has to be able to assert that the insertion happened.
    /// While this was <see langword="internal"/>, only <c>Rag.NET</c> itself could check either,
    /// and the satellites that could not — <c>UseRaptor</c>, <c>UseGraphRag</c>,
    /// <c>UseMindMapExtraction</c> — all registered behaviours that reached no pipeline (issue
    /// #191). The list is a snapshot view, not a handle: mutate the pipeline through
    /// <see cref="Add{T}"/>, <see cref="AddFirst{T}"/> and <see cref="Replace{TOld, TNew}"/>.
    /// </remarks>
    public IReadOnlyList<Type> GetBehaviorTypes() => _types.AsReadOnly();

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
