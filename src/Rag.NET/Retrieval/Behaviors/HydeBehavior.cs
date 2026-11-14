using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Retrieval.Behaviors;

[Singleton(As = typeof(HydeBehavior))]
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

        // Only building the HyDE context is guarded: a failure there degrades to the plain query.
        // `next` runs outside the try, so a failure further down the pipeline propagates once
        // instead of being taken for a HyDE failure and run a second time (#948).
        RetrievalContext continued;
        try
        {
            continued = await BuildHydeContextAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        // A deliberate refusal is not a model failure: it fails the query instead of degrading it (#948).
        catch (CallRefusedException) { throw; }
        catch (Exception ex)
        {
            RagPipelineLog.HydeGenerationFailed(ctx.Logger, ctx.Query, ex);
            continued = ctx with { Options = ctx.Options with { UseHyde = false } };
        }

        return await next(continued, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates the hypothesis or hypotheses and returns the context the rest of the pipeline
    /// runs with. Generation and embedding failures propagate to <see cref="HandleAsync"/>.
    /// </summary>
    private async Task<RetrievalContext> BuildHydeContextAsync(RetrievalContext ctx, CancellationToken ct)
    {
        var count = HydeOptions?.HypothesisCount ?? 1;
        if (count > 1 && Embedder is not null)
        {
            var (vector, doc) = await TryBuildAveragedEmbeddingAsync(ctx, count, ct).ConfigureAwait(false);
            if (vector is { } averaged)
                return ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingOverride = averaged } };

            // Averaging was not possible — fall back to the single-doc text path with an
            // already-generated hypothesis (no extra LLM call), or the plain query if none.
            return WithTextOverride(ctx, doc);
        }

        var single = await HydeGenerator!.GenerateAsync(ctx.Query, ct).ConfigureAwait(false);
        return WithTextOverride(ctx, single);
    }

    /// <summary>
    /// Returns the context with <paramref name="doc"/> as the embedding-text override.
    /// A null/whitespace document (the model produced nothing usable) is never propagated as an
    /// override — the pipeline falls through to embedding the plain query instead, with a log entry.
    /// </summary>
    private static RetrievalContext WithTextOverride(RetrievalContext ctx, string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc))
        {
            RagPipelineLog.HydeAveragingUnavailable(ctx.Logger, "all-hypotheses-empty", ctx.Query);
            return ctx with { Options = ctx.Options with { UseHyde = false } };
        }

        return ctx with { Options = ctx.Options with { UseHyde = false, EmbeddingTextOverride = doc } };
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
            return (null, null); // WithTextOverride logs "all-hypotheses-empty".

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
