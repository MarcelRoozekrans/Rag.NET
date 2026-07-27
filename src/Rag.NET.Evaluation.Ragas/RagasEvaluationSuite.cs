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
        Validate(samples);

        // Accumulate scores per metric across samples. Samples a metric could not score are
        // excluded from both sums, never averaged in as a zero — see the design's
        // "never fabricate a score".
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        var scored = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, _) in _metrics)
        {
            totals[name] = 0.0;
            scored[name] = 0;
        }

        foreach (var sample in samples)
        {
            // All metrics run concurrently per sample
            var scoreTasks = new Task<(string Name, double? Score)>[_metrics.Count];
            for (var i = 0; i < _metrics.Count; i++)
            {
                var m = _metrics[i];
                scoreTasks[i] = ScoreMetricAsync(m, sample, cancellationToken);
            }
            var results = await Task.WhenAll(scoreTasks).ConfigureAwait(false);
            foreach (var (name, score) in results)
            {
                if (score is not { } value)
                    continue;

                totals[name] += value;
                scored[name]++;
            }
        }

        var faithfulness     = Mean(totals, scored, "Faithfulness");
        var answerRelevance  = Mean(totals, scored, "AnswerRelevance");
        var contextPrecision = Mean(totals, scored, "ContextPrecision");
        var contextRecall    = Mean(totals, scored, "ContextRecall");

        return new RagasReport
        {
            Faithfulness     = faithfulness,
            AnswerRelevance  = answerRelevance,
            ContextPrecision = contextPrecision,
            ContextRecall    = contextRecall,
            OverallScore     = Overall(faithfulness, answerRelevance, contextPrecision, contextRecall),
        };
    }

    /// <summary>Rejects a run that cannot produce meaningful scores, before any LLM call.</summary>
    private void Validate(IReadOnlyList<EvaluationSample> samples)
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
        if (groundTruthMetricNames.Count == 0)
            return;

        var badSample = samples.FirstOrDefault(s => string.IsNullOrEmpty(s.ReferenceAnswer));
        if (badSample is not null)
            throw new InvalidOperationException(
                $"Metric(s) [{string.Join(", ", groundTruthMetricNames)}] require a non-empty " +
                $"{nameof(EvaluationSample.ReferenceAnswer)} on every sample. " +
                "Use DatasetGenerationMode.QuestionAndAnswer when building your evaluation dataset.");
    }

    /// <summary>Mean over the samples the metric could actually score; null when there were none.</summary>
    private static double? Mean(
        Dictionary<string, double> totals, Dictionary<string, int> scored, string name)
        => scored.TryGetValue(name, out var count) && count > 0 ? totals[name] / count : null;

    private static double Overall(params double?[] metrics)
    {
        var sum = 0.0;
        var count = 0;
        foreach (var metric in metrics)
        {
            if (metric is not { } value)
                continue;

            sum += value;
            count++;
        }

        return count > 0 ? sum / count : 0.0;
    }

    private static async Task<(string Name, double? Score)> ScoreMetricAsync(
        (string Name, IRagasMetric Metric) m,
        EvaluationSample sample,
        CancellationToken cancellationToken)
    {
        var score = await m.Metric.ScoreAsync(sample, cancellationToken).ConfigureAwait(false);
        return (m.Name, score);
    }
}
