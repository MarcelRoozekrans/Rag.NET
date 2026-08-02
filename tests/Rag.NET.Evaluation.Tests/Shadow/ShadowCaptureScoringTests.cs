using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Shadow;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// The reason the capture record exists at all: it must be feedable to <see cref="RagAbTester"/>
/// offline. These tests build the scorer's entire input — the sample list and both replay
/// pipelines — from nothing but <see cref="ShadowCapture"/> fields, so a record missing anything
/// the scorer consumes fails here rather than months after the captures were persisted.
/// </summary>
/// <remarks>
/// Scored by a reference-free scripted metric, because that is the offline reality too: production
/// captures carry no reference answer, so only the reference-free RAGAS metrics can score them
/// until someone adds references later.
/// </remarks>
public sealed class ShadowCaptureScoringTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompareAsync_OverReplayedCaptures_ScoresEachSidesCapturedAnswerAgainstItsCapturedContexts()
    {
        var captures = new[] { Capture("q1"), Capture("q2") };
        var seen = new List<EvaluationSample>();

        var report = await Tester(seen).CompareAsync(
            Variant(captures, c => c.Primary),
            Variant(captures, c => c.Secondary),
            Samples(captures),
            TestContext.Current.CancellationToken);

        // Two samples, two variants: four scored projections, each built from the captured answer
        // and the captured context texts of its own side. This is the whole contract — the metric
        // reads exactly what the decorator will have persisted.
        Assert.Equal(4, seen.Count);
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "primary answer to q1", StringComparison.Ordinal)
            && s.SourceChunks!.SequenceEqual(["primary context for q1"], StringComparer.Ordinal));
        Assert.Contains(seen, s => string.Equals(s.PredictedAnswer, "shadow answer to q1", StringComparison.Ordinal)
            && s.SourceChunks!.SequenceEqual(["shadow context for q1"], StringComparer.Ordinal));
        Assert.Equal(2, report.ComparableSamples);
        Assert.Equal("primary", report.VariantA);
        Assert.Equal("shadow", report.VariantB);
    }

    [Fact]
    public async Task CompareAsync_OverReplayedCaptures_CountsACapturedSecondaryFailureAsARunFailure()
    {
        // The secondary threw in production; the capture recorded it instead of dropping it.
        // Replayed, it must land under run failures — the same heading a live failure gets — so
        // the offline comparison is not biased toward whatever the secondary handled well.
        var captures = new[] { Capture("q1"), CaptureWithFailedSecondary("q2") };

        var report = await Tester([]).CompareAsync(
            Variant(captures, c => c.Primary),
            Variant(captures, c => c.Secondary),
            Samples(captures),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.SamplesRun);
        Assert.Equal(1, report.ComparableSamples);
        Assert.Equal(1, report.RunFailures);

        var failure = Assert.Single(report.Failures);
        Assert.Contains("shadow", failure, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: the vector store went away", failure, StringComparison.Ordinal);
    }

    private static RagAbTester Tester(List<EvaluationSample> seen) => new(
        new RagasEvaluationSuite([("Quality", new ScriptedMetric(seen))]),
        new AbOptions { Seed = 7 });

    private static ShadowCapture Capture(string question) => new(
        question,
        Answered("primary", question),
        Answered("shadow", question),
        Timestamp);

    private static ShadowCapture CaptureWithFailedSecondary(string question) => new(
        question,
        Answered("primary", question),
        new ShadowVariantCapture(
            "shadow",
            Answer: null,
            ContextTexts: [],
            Failure: new ShadowVariantFailure("shadow", "InvalidOperationException: the vector store went away")),
        Timestamp);

    private static ShadowVariantCapture Answered(string name, string question) => new(
        name,
        Answer: $"{name} answer to {question}",
        ContextTexts: [$"{name} context for {question}"],
        Latency: TimeSpan.FromMilliseconds(100),
        Spend: 0.002m);

    private static AbVariant Variant(
        ShadowCapture[] captures,
        Func<ShadowCapture, ShadowVariantCapture> side)
        => new(side(captures[0]).VariantName, new ReplayPipeline(captures, side));

    /// <summary>
    /// The dataset the scorer walks: the captured questions, with an empty reference answer
    /// because production has none. Reference-free metrics score it; the other two would refuse,
    /// exactly as the design says — until references are added to the stored pairs later.
    /// </summary>
    private static EvaluationSample[] Samples(ShadowCapture[] captures)
    {
        var samples = new EvaluationSample[captures.Length];
        for (var i = 0; i < captures.Length; i++)
            samples[i] = new EvaluationSample(captures[i].Question, PredictedAnswer: "", ReferenceAnswer: "");

        return samples;
    }

    /// <summary>A reference-free metric that records every projection it is asked to score.</summary>
    private sealed class ScriptedMetric(List<EvaluationSample> seen) : IRagasMetric
    {
        public bool RequiresGroundTruth => false;

        public Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
        {
            seen.Add(sample);
            return Task.FromResult<double?>(0.5);
        }
    }

    /// <summary>
    /// One side of the captured pairs, replayed as a pipeline: answers from storage, a throw where
    /// the capture recorded a failure. Built from <see cref="ShadowCapture"/> fields alone — the
    /// point of the test is that nothing else is needed.
    /// </summary>
    private sealed class ReplayPipeline(
        IReadOnlyList<ShadowCapture> captures,
        Func<ShadowCapture, ShadowVariantCapture> side) : IRagPipeline
    {
        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var capture = Find(query);
            var variant = side(capture);

            if (variant.Failure is { } failure)
                return Task.FromException<RagResponse>(new InvalidOperationException(failure.Message));

            var sources = new SearchResult[variant.ContextTexts.Count];
            for (var i = 0; i < sources.Length; i++)
            {
                sources[i] = new SearchResult
                {
                    // The scorer reads Chunk.Text only; document id and index are replay
                    // placeholders, which is why the capture does not need to store them.
                    Chunk = new TextChunk
                    {
                        Text = variant.ContextTexts[i],
                        DocumentId = new DocumentId("captured"),
                        ChunkIndex = i,
                    },
                    Score = 1.0,
                };
            }

            return Task.FromResult(new RagResponse { Answer = variant.Answer!, Sources = sources });
        }

        private ShadowCapture Find(string question)
        {
            for (var i = 0; i < captures.Count; i++)
            {
                if (string.Equals(captures[i].Question, question, StringComparison.Ordinal))
                    return captures[i];
            }

            throw new InvalidOperationException($"No capture recorded for '{question}'.");
        }

        public Task<Result<IngestionResult, RagError>> IngestAsync(
            Stream document,
            DocumentMetadata metadata,
            IngestionOptions? options = null,
            IProgress<IngestionProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
