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
        typeof(ParentDocumentIngestionBehavior),
        typeof(EmbeddingBehavior),
        typeof(StorageBehavior),
    ];

    public IngestionPipelineBuilder Add<T>(Type? after = null, Type? before = null)
        where T : IIngestionBehavior
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

    public IngestionPipelineBuilder Replace<TOld, TNew>()
        where TOld : IIngestionBehavior
        where TNew : IIngestionBehavior
    {
        var idx = _types.IndexOf(typeof(TOld));
        if (idx >= 0) _types[idx] = typeof(TNew);
        return this;
    }

    internal IReadOnlyList<Type> GetBehaviorTypes() => _types.AsReadOnly();

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
