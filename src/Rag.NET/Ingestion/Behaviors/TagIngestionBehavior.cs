using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using ZeroAlloc.Inject;

namespace Rag.NET.Ingestion.Behaviors;

/// <summary>
/// Embeds tag values from <see cref="Rag.NET.Models.DocumentMetadata.Tags"/> and stores
/// them in <see cref="ITagIndex"/> for use by <see cref="Rag.NET.Retrieval.TagRetriever"/>.
/// No-op when <see cref="ITagIndex"/> is not registered.
/// </summary>
[Singleton]
public sealed class TagIngestionBehavior : IIngestionBehavior
{
    [Inject(Required = false)] public ITagIndex? TagIndex { get; set; }
    [Inject(Required = false)] public IEmbeddingGenerator<string, Embedding<float>>? Embedder { get; set; }
    [Inject(Required = false)] public ILogger<TagIngestionBehavior>? Logger { get; set; }

    public async ValueTask<IngestionResult> HandleAsync(
        IngestionContext ctx, CancellationToken ct,
        Func<IngestionContext, CancellationToken, ValueTask<IngestionResult>> next)
    {
        if (TagIndex is null || Embedder is null || ctx.Metadata.Tags.Count == 0)
            return await next(ctx, ct).ConfigureAwait(false);

        foreach (var (key, tagValue) in ctx.Metadata.Tags)
        {
            // The tag index matches natural-language queries against tag values semantically, so
            // it stores the value's textual form; MetadataValue.ToString is invariant and
            // lossless as text for every kind.
            var value = tagValue.ToString();
            if (TagIndex.Contains(key, value))
                continue;

            try
            {
                var embeddings = await Embedder
                    .GenerateAsync([value], cancellationToken: ct)
                    .ConfigureAwait(false);
                TagIndex.Add(key, value, embeddings[0].Vector);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex,
                    "Failed to embed tag '{Key}={Value}'; skipping", key, value);
            }
        }

        return await next(ctx, ct).ConfigureAwait(false);
    }
}
