using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Fluent builder for <see cref="RagasEvaluationSuite"/>.
/// Register only the metrics you need — each adds LLM calls at evaluation time.
/// </summary>
public sealed class RagasEvaluationSuiteBuilder(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    private bool _faithfulness;
    private bool _answerRelevance;
    private bool _contextPrecision;
    private bool _contextRecall;

    public RagasEvaluationSuiteBuilder AddFaithfulness()     { _faithfulness     = true; return this; }
    public RagasEvaluationSuiteBuilder AddAnswerRelevance()  { _answerRelevance  = true; return this; }
    public RagasEvaluationSuiteBuilder AddContextPrecision() { _contextPrecision = true; return this; }
    public RagasEvaluationSuiteBuilder AddContextRecall()    { _contextRecall    = true; return this; }

    /// <summary>
    /// Builds the suite. Validation of ground-truth requirements happens at
    /// <see cref="RagasEvaluationSuite.EvaluateAsync"/> time — fail fast on the first
    /// sample with an empty ReferenceAnswer when Context Precision or Recall is registered.
    /// </summary>
    public RagasEvaluationSuite Build()
    {
        var metrics = new List<(string Name, IRagasMetric Metric)>();
        if (_faithfulness)     metrics.Add(("Faithfulness",     new FaithfulnessEvaluator(chatClient)));
        if (_answerRelevance)  metrics.Add(("AnswerRelevance",  new AnswerRelevanceEvaluator(chatClient, embeddingGenerator)));
        if (_contextPrecision) metrics.Add(("ContextPrecision", new ContextPrecisionEvaluator(chatClient)));
        if (_contextRecall)    metrics.Add(("ContextRecall",    new ContextRecallEvaluator(chatClient)));
        return new RagasEvaluationSuite(metrics);
    }
}
