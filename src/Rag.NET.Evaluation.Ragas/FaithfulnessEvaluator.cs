using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas.Judging;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Faithfulness: fraction of claims in the predicted answer that are supported by the retrieved
/// source chunks. Score = supported_claims / readable_claim_verdicts (0–1, higher = more
/// grounded), or <c>null</c> when the sample cannot be scored.
/// </summary>
/// <remarks>
/// A reply the claim extractor could not read scores <c>null</c>, not 1.0. Before Phase 3.1 a
/// malformed JSON reply was caught, became an empty claim list, and scored the best possible
/// value — indistinguishable from a genuinely perfect answer. An answer that genuinely asserts
/// nothing still scores 1.0, because nothing unfaithful was asserted; the difference is that the
/// two cases are now distinguishable.
/// </remarks>
public sealed class FaithfulnessEvaluator : IRagasMetric
{
    private const string ExtractClaimsPrompt =
        "Extract all atomic factual claims from the answer. " +
        "Output a JSON array of strings — one string per claim. No explanation.";

    private const string VerifyClaimPrompt =
        "Is the following claim supported by the provided context? " +
        "Respond with exactly \"yes\" or \"no\" and nothing else.";

    private readonly RagasJudge _judge;

    /// <summary>Creates an evaluator that judges with its own <see cref="IChatClient"/>.</summary>
    /// <param name="chatClient">The model asked to extract and verify claims.</param>
    /// <param name="options">Run tuning; defaults are used when omitted.</param>
    /// <param name="costLedger">Optional ledger for the LLM spend this metric incurs.</param>
    public FaithfulnessEvaluator(
        IChatClient chatClient,
        RagasOptions? options = null,
        ICostLedger? costLedger = null)
        : this(new RagasJudge(chatClient, options ?? new RagasOptions(), costLedger))
    {
    }

    /// <summary>Creates an evaluator sharing a judge — and so a concurrency ceiling — with others.</summary>
    /// <param name="judge">The shared judge.</param>
    internal FaithfulnessEvaluator(RagasJudge judge) => _judge = judge;

    /// <inheritdoc />
    public bool RequiresGroundTruth => false;

    /// <inheritdoc />
    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        // Nothing retrieved is an absence of evidence, not evidence of an ungrounded answer.
        if (sample.SourceChunks is not { Count: > 0 })
            return null;

        var claims = await _judge
            .ExtractListAsync(ExtractClaimsPrompt, sample.PredictedAnswer, cancellationToken)
            .ConfigureAwait(false);

        if (!claims.Parsed)
            return null;

        if (claims.Items.Count == 0)
            return 1.0;

        var context = string.Join("\n", sample.SourceChunks);
        var verdicts = await _judge.ClassifyManyAsync(
                VerifyClaimPrompt,
                claims.Items,
                claim => $"Context: {context}\nClaim: {claim}",
                cancellationToken)
            .ConfigureAwait(false);

        return RagasMath.ScoreFromVerdicts(verdicts);
    }
}
