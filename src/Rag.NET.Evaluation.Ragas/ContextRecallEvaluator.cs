using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas.Judging;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Context Recall: fraction of ground-truth statements supported by the retrieved chunks.
/// Score = supported_statements / readable_statement_verdicts (0–1, higher = better coverage),
/// or <c>null</c> when the sample cannot be scored.
/// Requires a non-empty <see cref="EvaluationSample.ReferenceAnswer"/>.
/// </summary>
/// <remarks>
/// The same arithmetic as Faithfulness over different material, and it shares
/// <see cref="RagasMath.ScoreFromVerdicts"/> rather than carrying its own copy — the two copies
/// are how the identical parse-failure defect came to exist in both before Phase 3.1.
/// </remarks>
public sealed class ContextRecallEvaluator : IRagasMetric
{
    private const string ExtractStatementsPrompt =
        "Extract all atomic statements from the reference answer. " +
        "Output a JSON array of strings — one string per statement. No explanation.";

    private const string VerifyStatementPrompt =
        "Is the following statement supported by the provided context? " +
        "Respond with exactly \"yes\" or \"no\" and nothing else.";

    private readonly RagasJudge _judge;

    /// <summary>Creates an evaluator that judges with its own <see cref="IChatClient"/>.</summary>
    /// <param name="chatClient">The model asked to extract and verify statements.</param>
    /// <param name="options">Run tuning; defaults are used when omitted.</param>
    /// <param name="costLedger">Optional ledger for the LLM spend this metric incurs.</param>
    public ContextRecallEvaluator(
        IChatClient chatClient,
        RagasOptions? options = null,
        ICostLedger? costLedger = null)
        : this(new RagasJudge(chatClient, options ?? new RagasOptions(), costLedger))
    {
    }

    /// <summary>Creates an evaluator sharing a judge — and so a concurrency ceiling — with others.</summary>
    /// <param name="judge">The shared judge.</param>
    internal ContextRecallEvaluator(RagasJudge judge) => _judge = judge;

    /// <inheritdoc />
    public bool RequiresGroundTruth => true;

    /// <inheritdoc />
    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (string.IsNullOrEmpty(sample.ReferenceAnswer))
            throw new InvalidOperationException(
                $"ContextRecallEvaluator requires a non-empty {nameof(EvaluationSample.ReferenceAnswer)}. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");

        // Nothing retrieved is an absence of evidence, not evidence of missed coverage.
        if (sample.SourceChunks is not { Count: > 0 })
            return null;

        var statements = await _judge
            .ExtractListAsync(ExtractStatementsPrompt, sample.ReferenceAnswer, cancellationToken)
            .ConfigureAwait(false);

        if (!statements.Parsed)
            return null;

        if (statements.Items.Count == 0)
            return 1.0;

        var context = string.Join("\n", sample.SourceChunks);
        var verdicts = await _judge.ClassifyManyAsync(
                VerifyStatementPrompt,
                statements.Items,
                statement => $"Context: {context}\nStatement: {statement}",
                cancellationToken)
            .ConfigureAwait(false);

        return RagasMath.ScoreFromVerdicts(verdicts);
    }
}
