using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Runs registered RAGAS metrics over a set of evaluation samples.
/// Metrics execute concurrently per sample; samples are processed sequentially.
/// </summary>
/// <remarks>
/// This class is designed to be constructed exclusively via <see cref="RagasEvaluationSuiteBuilder"/>.
/// The metric name strings used in <see cref="EvaluateAsync"/> correspond to the names registered by the builder.
/// </remarks>
public sealed class RagasEvaluationSuite
{
    private readonly IReadOnlyList<(string Name, IRagasMetric Metric)> _metrics;

    internal RagasEvaluationSuite(IReadOnlyList<(string Name, IRagasMetric Metric)> metrics)
    {
        _metrics = metrics;
    }

    /// <summary>
    /// Evaluates all samples. Throws <see cref="InvalidOperationException"/> on the first
    /// sample with an empty ReferenceAnswer when a ground-truth metric is registered.
    /// All registered metrics run concurrently per sample; samples processed sequentially.
    /// </summary>
    public async Task<RagasReport> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (_metrics.Count == 0)
            throw new InvalidOperationException("No metrics are registered.");

        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));

        // Fail fast: validate all samples against registered metrics before any LLM call
        var groundTruthMetricNames = _metrics
            .Where(m => m.Metric.RequiresGroundTruth)
            .Select(m => m.Name)
            .ToList();
        if (groundTruthMetricNames.Count > 0)
        {
            var badSample = samples.FirstOrDefault(s => string.IsNullOrEmpty(s.ReferenceAnswer));
            if (badSample is not null)
                throw new InvalidOperationException(
                    $"Metric(s) [{string.Join(", ", groundTruthMetricNames)}] require a non-empty " +
                    $"{nameof(EvaluationSample.ReferenceAnswer)} on every sample. " +
                    "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");
        }

        // Accumulate scores per metric across samples
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, _) in _metrics)
            totals[name] = 0.0;

        foreach (var sample in samples)
        {
            // All metrics run concurrently per sample
            var scoreTasks = new Task<(string Name, double Score)>[_metrics.Count];
            for (var i = 0; i < _metrics.Count; i++)
            {
                var m = _metrics[i];
                scoreTasks[i] = ScoreMetricAsync(m, sample, cancellationToken);
            }
            var results = await Task.WhenAll(scoreTasks).ConfigureAwait(false);
            foreach (var (name, score) in results)
                totals[name] += score;
        }

        double? faithfulness     = totals.TryGetValue("Faithfulness",     out var f) ? f / samples.Count : null;
        double? answerRelevance  = totals.TryGetValue("AnswerRelevance",  out var a) ? a / samples.Count : null;
        double? contextPrecision = totals.TryGetValue("ContextPrecision", out var p) ? p / samples.Count : null;
        double? contextRecall    = totals.TryGetValue("ContextRecall",    out var r) ? r / samples.Count : null;

        var registered = new[] { faithfulness, answerRelevance, contextPrecision, contextRecall }
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var overallScore = registered.Count > 0 ? registered.Average() : 0.0;

        return new RagasReport
        {
            Faithfulness     = faithfulness,
            AnswerRelevance  = answerRelevance,
            ContextPrecision = contextPrecision,
            ContextRecall    = contextRecall,
            OverallScore     = overallScore,
        };
    }

    private static async Task<(string Name, double Score)> ScoreMetricAsync(
        (string Name, IRagasMetric Metric) m,
        EvaluationSample sample,
        CancellationToken cancellationToken)
    {
        var score = await m.Metric.ScoreAsync(sample, cancellationToken).ConfigureAwait(false);
        return (m.Name, score);
    }
}
