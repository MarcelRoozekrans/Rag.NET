using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Pipeline;

namespace Rag.NET.DependencyInjection;

public sealed class IngestionPipelineBuilder
{
    private readonly List<Type> _types =
    [
        typeof(OverwriteBehavior),
        typeof(ParseBehavior),
        typeof(ChunkingBehavior),
        typeof(LlmMetadataExtractionBehavior),
        typeof(MetadataBehavior),
        typeof(TagIngestionBehavior),
        typeof(ChunkSanitiserBehavior),
        typeof(ParentDocumentIngestionBehavior),
        typeof(EmbeddingBehavior),
        typeof(SparseEmbeddingBehavior),
        typeof(StorageBehavior),
    ];

    /// <summary>
    /// Inserts <typeparamref name="T"/> into the chain, after or before an anchor type.
    /// </summary>
    /// <typeparam name="T">The behaviour to insert.</typeparam>
    /// <param name="after">Insert directly after this type.</param>
    /// <param name="before">Insert directly before this type. Ignored when <paramref name="after"/> is set.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// Idempotent, and for the same reason <see cref="RetrievalPipelineBuilder.AddFirst{T}"/> is:
    /// a behaviour reached from two directions would be inserted twice, and <see cref="Build"/>
    /// resolves the same singleton for both slots, so it would run twice per document with
    /// nothing about the container looking wrong. Two directions is now the normal case — a
    /// <c>Use*</c> method places its own behaviour, and the caller may also name it in
    /// <c>AddRagNet</c>'s <c>ingestion:</c> delegate, as <c>docs/guide/raptor.md</c> and
    /// <c>docs/guide/graphrag.md</c> teach. <b>The first insertion wins the position</b>, and
    /// those delegates run before <c>configure</c> does, so an explicit placement beats the
    /// <c>Use*</c> default rather than being silently overridden by it.
    /// </remarks>
    public IngestionPipelineBuilder Add<T>(Type? after = null, Type? before = null)
        where T : IIngestionBehavior
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

    public IngestionPipelineBuilder Replace<TOld, TNew>()
        where TOld : IIngestionBehavior
        where TNew : IIngestionBehavior
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
    /// <see cref="Add{T}"/> and <see cref="Replace{TOld, TNew}"/>.
    /// </remarks>
    public IReadOnlyList<Type> GetBehaviorTypes() => _types.AsReadOnly();

    public Pipeline<IngestionContext, IngestionResult> Build(IServiceProvider sp)
    {
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> chain =
            static (ctx, _) => ValueTask.FromResult(
                new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = 0 });

        for (var i = _types.Count - 1; i >= 0; i--)
        {
            var behavior = (IIngestionBehavior)sp.GetRequiredService(_types[i]);
            var next = chain;
            chain = (ctx, ct) => behavior.HandleAsync(ctx, ct, next);
        }

        return new Pipeline<IngestionContext, IngestionResult>(chain);
    }
}
