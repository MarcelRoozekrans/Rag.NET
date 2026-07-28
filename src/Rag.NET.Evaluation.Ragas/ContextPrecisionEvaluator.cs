using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas.Judging;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Context Precision: rank-aware average precision over the retrieved chunks,
/// <c>Σ(Precision@k × rel_k) / total_relevant</c> (0–1, higher = the relevant chunks were
/// ranked higher), or <c>null</c> when the sample cannot be scored.
/// Requires a non-empty <see cref="EvaluationSample.ReferenceAnswer"/>.
/// </summary>
/// <remarks>
/// Rank-aware since Phase 3.1. The previous implementation computed <c>relevant / total</c> and
/// ignored order entirely, so a retriever that returned the gold chunk first scored identically
/// to one that returned it last — precisely the discrimination this metric exists to provide.
/// Scores from before that change are not comparable with scores after it.
/// </remarks>
public sealed class ContextPrecisionEvaluator : IRagasMetric
{
    private const string RelevancePrompt =
        "Is the following context chunk useful for answering the question, given the reference " +
        "answer? Respond with exactly \"yes\" or \"no\" and nothing else.";

    private readonly RagasJudge _judge;

    /// <summary>Creates an evaluator that judges with its own <see cref="IChatClient"/>.</summary>
    /// <param name="chatClient">The model asked to judge chunk relevance.</param>
    /// <param name="options">Run tuning; defaults are used when omitted.</param>
    /// <param name="costLedger">Optional ledger for the LLM spend this metric incurs.</param>
    public ContextPrecisionEvaluator(
        IChatClient chatClient,
        RagasOptions? options = null,
        ICostLedger? costLedger = null)
        : this(new RagasJudge(chatClient, options ?? new RagasOptions(), costLedger))
    {
    }

    /// <summary>Creates an evaluator sharing a judge — and so a concurrency ceiling — with others.</summary>
    /// <param name="judge">The shared judge.</param>
    internal ContextPrecisionEvaluator(RagasJudge judge) => _judge = judge;

    /// <inheritdoc />
    public bool RequiresGroundTruth => true;

    /// <inheritdoc />
    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (string.IsNullOrEmpty(sample.ReferenceAnswer))
            throw new InvalidOperationException(
                $"ContextPrecisionEvaluator requires a non-empty {nameof(EvaluationSample.ReferenceAnswer)}. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");

        // Nothing retrieved cannot be ranked well or badly.
        if (sample.SourceChunks is not { Count: > 0 } chunks)
            return null;

        // ClassifyManyAsync preserves input order, which is why it exists: verdict k must belong
        // to the chunk retrieved at rank k, or the ranks the formula depends on are meaningless.
        var verdicts = await _judge.ClassifyManyAsync(
                RelevancePrompt,
                chunks,
                chunk => $"Question: {sample.Question}\n" +
                         $"Reference Answer: {sample.ReferenceAnswer}\n" +
                         $"Context Chunk: {chunk}",
                cancellationToken)
            .ConfigureAwait(false);

        return RagasMath.PrecisionFromVerdicts(verdicts);
    }
}
