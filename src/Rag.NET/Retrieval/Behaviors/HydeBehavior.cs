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
    [Inject(Required = false)] public HydeOptions? HydeOptions { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseHyde || HydeGenerator is null)
            return await next(ctx, ct).ConfigureAwait(false);

        try
        {
            var count = HydeOptions?.HypothesisCount ?? 1;
            if (count > 1 && Embedder is not null)
            {
                var (vector, doc) = await TryBuildAveragedEmbeddingAsync(ctx, count, ct).ConfigureAwait(false);
                if (vector is { } averaged)
                {
                    return await next(
                        ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingOverride = averaged } },
                        ct).ConfigureAwait(false);
                }

                // Averaging was not possible — fall back to the single-doc text path with an
                // already-generated hypothesis (no extra LLM call), or the plain query if none.
                return await ContinueWithTextAsync(ctx, doc, ct, next).ConfigureAwait(false);
            }

            var single = await HydeGenerator.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
            return await ContinueWithTextAsync(ctx, single, ct, next).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.HydeGenerationFailed(ctx.Logger, ctx.Query, ex);
            return await next(ctx with { Options = ctx.Options with { UseHyde = false } }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Continues the pipeline with <paramref name="doc"/> as the embedding-text override.
    /// A null/whitespace document (the model produced nothing usable) is never propagated as an
    /// override — the pipeline falls through to embedding the plain query instead, with a log entry.
    /// </summary>
    private static async ValueTask<IReadOnlyList<SearchResult>> ContinueWithTextAsync(
        RetrievalContext ctx, string? doc, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            RagPipelineLog.HydeAveragingUnavailable(ctx.Logger, "all-hypotheses-empty", ctx.Query);
            return await next(ctx with { Options = ctx.Options with { UseHyde = false } }, ct).ConfigureAwait(false);
        }

        return await next(
            ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = doc } },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates up to <paramref name="count"/> hypotheses, embeds them in one batch, and
    /// returns the L2-normalized mean embedding. When averaging is impossible (no or one
    /// surviving hypothesis, empty embedding batch, dimension mismatch, zero-norm mean) it
    /// returns a null vector plus the first hypothesis text (when available), so the caller
    /// can fall back to the single-doc text path without another LLM call.
    /// Exceptions propagate to the caller's fallback catch.
    /// </summary>
    private async Task<(ReadOnlyMemory<float>? Vector, string? Doc)> TryBuildAveragedEmbeddingAsync(
        RetrievalContext ctx, int count, CancellationToken ct)
    {
        var docs = await HydeGenerator!.GenerateManyAsync(ctx.Query, count, ct).ConfigureAwait(false);
        if (docs.Count < count)
            RagPipelineLog.HydePartialHypotheses(ctx.Logger, docs.Count, count, ctx.Query);

        if (docs.Count == 0)
            return (null, null); // ContinueWithTextAsync logs "all-hypotheses-empty".

        if (docs.Count == 1)
        {
            RagPipelineLog.HydeAveragingUnavailable(ctx.Logger, "single-survivor", ctx.Query);
            return (null, docs[0]);
        }

        var embeddings = await Embedder!.GenerateAsync(docs, cancellationToken: ct).ConfigureAwait(false);
        if (embeddings.Count == 0)
        {
            RagPipelineLog.HydeAveragingUnavailable(ctx.Logger, "empty-embedding-batch", ctx.Query);
            return (null, docs[0]);
        }

        var (vector, failureReason) = AverageAndNormalize(embeddings);
        if (vector is null)
            RagPipelineLog.HydeAveragingUnavailable(ctx.Logger, failureReason!, ctx.Query);

        return (vector, docs[0]);
    }

    /// <summary>
    /// Mean-pools the embedding vectors, then L2-normalizes (double accumulation for the norm).
    /// Returns a null vector plus the failure reason on dimension mismatch or a zero-norm mean —
    /// a null (fall back to text embedding) beats a meaningless zero vector.
    /// </summary>
    private static (ReadOnlyMemory<float>? Vector, string? FailureReason) AverageAndNormalize(
        GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        var dimension = embeddings[0].Vector.Length;
        if (dimension == 0)
            return (null, "zero-norm");

        var pooled = new float[dimension];
        for (var i = 0; i < embeddings.Count; i++)
        {
            var span = embeddings[i].Vector.Span;
            if (span.Length != dimension)
                return (null, "dimension-mismatch");
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
            return (null, "zero-norm");

        var norm = (float)Math.Sqrt(normSquared);
        foreach (ref var value in pooled.AsSpan())
            value /= norm;

        return (new ReadOnlyMemory<float>(pooled), null);
    }
}
