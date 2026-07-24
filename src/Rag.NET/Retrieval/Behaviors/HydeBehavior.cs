using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton]
public sealed class HydeBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IHypotheticalDocumentGenerator? HydeGenerator { get; set; }
    [Inject(Required = false)] public IEmbeddingGenerator<string, Embedding<float>>? Embedder { get; set; }
    [Inject(Required = false)] public HydeOptions? Options { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseHyde || HydeGenerator is null)
            return await next(ctx, ct).ConfigureAwait(false);

        try
        {
            var count = Options?.HypothesisCount ?? 1;
            if (count > 1 && Embedder is not null)
            {
                var (vector, doc) = await TryBuildAveragedEmbeddingAsync(ctx.Query, count, ct).ConfigureAwait(false);
                if (vector is { } averaged)
                {
                    return await next(
                        ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingOverride = averaged } },
                        ct).ConfigureAwait(false);
                }

                // Averaging was not possible (zero-norm mean, dimension mismatch, …) —
                // fall back to the single-doc text path with an already-generated hypothesis.
                return await next(
                    ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = doc } },
                    ct).ConfigureAwait(false);
            }

            var single = await HydeGenerator.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
            return await next(
                ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = single } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.HydeGenerationFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx with { Options = ctx.Options with { UseHyde = false } }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generates up to <paramref name="count"/> hypotheses, embeds them in one batch, and
    /// returns the L2-normalized mean embedding. When averaging is impossible (empty batch,
    /// dimension mismatch, zero-norm mean) it returns the first hypothesis text instead, so
    /// the caller can fall back to the single-doc text path without another LLM call.
    /// Exceptions propagate to the caller's fallback catch.
    /// </summary>
    private async Task<(ReadOnlyMemory<float>? Vector, string Doc)> TryBuildAveragedEmbeddingAsync(
        string query, int count, CancellationToken ct)
    {
        var docs = await HydeGenerator!.GenerateManyAsync(query, count, ct).ConfigureAwait(false);
        if (docs.Count == 0)
            return (null, string.Empty);
        if (docs.Count == 1)
            return (null, docs[0]);

        var embeddings = await Embedder!.GenerateAsync(docs, cancellationToken: ct).ConfigureAwait(false);
        if (embeddings.Count == 0)
            return (null, docs[0]);

        var vector = AverageAndNormalize(embeddings);
        return (vector, docs[0]);
    }

    /// <summary>
    /// Mean-pools the embedding vectors, then L2-normalizes (double accumulation for the norm).
    /// Returns <see langword="null"/> on dimension mismatch or a zero-norm mean — a null
    /// (fall back to text embedding) beats a meaningless zero vector.
    /// </summary>
    private static ReadOnlyMemory<float>? AverageAndNormalize(GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        var dimension = embeddings[0].Vector.Length;
        if (dimension == 0)
            return null;

        var pooled = new float[dimension];
        for (var i = 0; i < embeddings.Count; i++)
        {
            var span = embeddings[i].Vector.Span;
            if (span.Length != dimension)
                return null;
            for (var d = 0; d < dimension; d++)
                pooled[d] += span[d];
        }

        var embeddingsCount = embeddings.Count;
        double normSquared = 0;
        foreach (ref var value in pooled.AsSpan())
        {
            value /= embeddingsCount;
            normSquared += (double)value * value;
        }

        if (normSquared == 0)
            return null;

        var norm = (float)Math.Sqrt(normSquared);
        foreach (ref var value in pooled.AsSpan())
            value /= norm;

        return new ReadOnlyMemory<float>(pooled);
    }
}
