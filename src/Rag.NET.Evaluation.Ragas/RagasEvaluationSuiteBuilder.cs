using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas.Judging;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Fluent builder for <see cref="RagasEvaluationSuite"/>.
/// Register only the metrics you need — each adds LLM calls at evaluation time.
/// </summary>
/// <remarks>
/// Every metric the builder registers shares one judge, and therefore one
/// <see cref="RagasOptions.MaxConcurrentCalls"/> ceiling and one cost ledger for the whole run.
/// A per-metric ceiling multiplies: four metrics each fanning out over a 50-chunk sample at a
/// ceiling of 2 is 8 requests in flight, not 2, and the number a caller sets is then not the
/// number they get.
/// </remarks>
public sealed class RagasEvaluationSuiteBuilder
{
    private readonly RagasJudge _judge;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly int _syntheticQuestionCount;

    private bool _faithfulness;
    private bool _answerRelevance;
    private bool _contextPrecision;
    private bool _contextRecall;

    /// <summary>Creates a builder whose metrics will share one judge.</summary>
    /// <param name="chatClient">The model every metric asks for judgements.</param>
    /// <param name="embeddingGenerator">Embeds questions for Answer Relevance.</param>
    /// <param name="options">Run tuning; defaults are used when omitted.</param>
    /// <param name="costLedger">
    /// Optional ledger for the run's LLM spend. Recorded entries cost zero unless
    /// <see cref="RagasOptions.PricePerInputToken"/> and
    /// <see cref="RagasOptions.PricePerOutputToken"/> are set.
    /// </param>
    public RagasEvaluationSuiteBuilder(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        RagasOptions? options = null,
        ICostLedger? costLedger = null)
    {
        var resolved = options ?? new RagasOptions();

        _judge = new RagasJudge(chatClient, resolved, costLedger);
        _embeddingGenerator = embeddingGenerator;
        _syntheticQuestionCount = resolved.SyntheticQuestionCount;
    }

    /// <summary>Registers Faithfulness. No <c>ReferenceAnswer</c> required.</summary>
    public RagasEvaluationSuiteBuilder AddFaithfulness()     { _faithfulness     = true; return this; }

    /// <summary>Registers Answer Relevance. No <c>ReferenceAnswer</c> required.</summary>
    public RagasEvaluationSuiteBuilder AddAnswerRelevance()  { _answerRelevance  = true; return this; }

    /// <summary>Registers Context Precision. Requires a non-empty <c>ReferenceAnswer</c>.</summary>
    public RagasEvaluationSuiteBuilder AddContextPrecision() { _contextPrecision = true; return this; }

    /// <summary>Registers Context Recall. Requires a non-empty <c>ReferenceAnswer</c>.</summary>
    public RagasEvaluationSuiteBuilder AddContextRecall()    { _contextRecall    = true; return this; }

    /// <summary>
    /// Builds the suite. Validation of ground-truth requirements happens at
    /// <see cref="RagasEvaluationSuite.EvaluateAsync"/> time — fail fast on the first
    /// sample with an empty ReferenceAnswer when Context Precision or Recall is registered.
    /// </summary>
    public RagasEvaluationSuite Build()
    {
        var metrics = new List<(string Name, IRagasMetric Metric)>();

        if (_faithfulness)
            metrics.Add((RagasMetricNames.Faithfulness, new FaithfulnessEvaluator(_judge)));

        if (_answerRelevance)
            metrics.Add((
                RagasMetricNames.AnswerRelevance,
                new AnswerRelevanceEvaluator(_judge, _embeddingGenerator, _syntheticQuestionCount)));

        if (_contextPrecision)
            metrics.Add((RagasMetricNames.ContextPrecision, new ContextPrecisionEvaluator(_judge)));

        if (_contextRecall)
            metrics.Add((RagasMetricNames.ContextRecall, new ContextRecallEvaluator(_judge)));

        return new RagasEvaluationSuite(metrics);
    }
}
