using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Runs registered RAGAS metrics over a set of evaluation samples.
/// Metrics execute concurrently per sample; samples are processed sequentially.
/// </summary>
/// <remarks>
/// This class is designed to be constructed exclusively via <see cref="RagasEvaluationSuiteBuilder"/>.
/// The metric name strings keying <see cref="RagasReport.UnscoreableSamples"/> and
/// <see cref="RagasSampleScore.Scores"/> are the ones the builder registers.
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
    /// <param name="samples">The samples to score.</param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    public async Task<RagasReport> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        Validate(samples);

        var aggregate = new Aggregate(_metrics, samples.Count);
        foreach (var sample in samples)
        {
            var results = await ScoreSampleAsync(sample, cancellationToken).ConfigureAwait(false);
            aggregate.Add(sample, results);
        }

        return aggregate.ToReport();
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

    /// <summary>Starts every metric for one sample before awaiting any of them.</summary>
    /// <remarks>
    /// Starting them all first is what makes the shared ceiling observable: the metrics contend
    /// for the same semaphore, so the limit applies to the run rather than to each metric.
    /// </remarks>
    private Task<(string Name, double? Score)[]> ScoreSampleAsync(
        EvaluationSample sample, CancellationToken cancellationToken)
    {
        var scoreTasks = new Task<(string Name, double? Score)>[_metrics.Count];
        for (var i = 0; i < _metrics.Count; i++)
            scoreTasks[i] = ScoreMetricAsync(_metrics[i], sample, cancellationToken);

        return Task.WhenAll(scoreTasks);
    }

    private static async Task<(string Name, double? Score)> ScoreMetricAsync(
        (string Name, IRagasMetric Metric) m,
        EvaluationSample sample,
        CancellationToken cancellationToken)
    {
        var score = await m.Metric.ScoreAsync(sample, cancellationToken).ConfigureAwait(false);
        return (m.Name, score);
    }

    /// <summary>Mean of the metrics that produced a score; null when none did.</summary>
    /// <remarks>
    /// Null rather than zero, for the same reason an unscoreable sample is excluded rather than
    /// counted: a run nothing could be scored in has no overall quality, and <c>0.0</c> would
    /// report the worst possible one.
    /// </remarks>
    private static double? Overall(params double?[] metrics)
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

        return count > 0 ? sum / count : null;
    }

    /// <summary>
    /// Folds each sample's scores into the running totals, the unscoreable counts, and the
    /// per-sample list the report carries.
    /// </summary>
    /// <remarks>
    /// A separate type rather than a handful of dictionaries threaded through helper parameters:
    /// the three collections are only correct when they are updated together, and keeping them
    /// behind one <see cref="Add"/> is what makes that structural rather than remembered.
    /// </remarks>
    private sealed class Aggregate
    {
        private readonly Dictionary<string, double> _totals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _scored = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unscoreable = new(StringComparer.Ordinal);
        private readonly List<RagasSampleScore> _samples;

        public Aggregate(IReadOnlyList<(string Name, IRagasMetric Metric)> metrics, int sampleCount)
        {
            _samples = new List<RagasSampleScore>(sampleCount);
            foreach (var (name, _) in metrics)
            {
                _totals[name] = 0.0;
                _scored[name] = 0;

                // Seeded so a metric that scored everything still reports a zero count, rather
                // than an absent key the caller has to read as either "none" or "not registered".
                _unscoreable[name] = 0;
            }
        }

        public void Add(EvaluationSample sample, (string Name, double? Score)[] results)
        {
            var scores = new Dictionary<string, double?>(results.Length, StringComparer.Ordinal);
            foreach (var (name, score) in results)
            {
                scores[name] = score;
                if (score is { } value)
                {
                    _totals[name] += value;
                    _scored[name]++;
                }
                else
                {
                    _unscoreable[name]++;
                }
            }

            _samples.Add(new RagasSampleScore(sample.Question, scores));
        }

        public RagasReport ToReport()
        {
            var faithfulness     = Mean(RagasMetricNames.Faithfulness);
            var answerRelevance  = Mean(RagasMetricNames.AnswerRelevance);
            var contextPrecision = Mean(RagasMetricNames.ContextPrecision);
            var contextRecall    = Mean(RagasMetricNames.ContextRecall);

            return new RagasReport
            {
                Faithfulness       = faithfulness,
                AnswerRelevance    = answerRelevance,
                ContextPrecision   = contextPrecision,
                ContextRecall      = contextRecall,
                OverallScore       = Overall(faithfulness, answerRelevance, contextPrecision, contextRecall),
                Samples            = _samples,
                UnscoreableSamples = _unscoreable,
            };
        }

        /// <summary>Mean over the samples the metric could actually score; null when there were none.</summary>
        private double? Mean(string name)
            => _scored.TryGetValue(name, out var count) && count > 0 ? _totals[name] / count : null;
    }
}
