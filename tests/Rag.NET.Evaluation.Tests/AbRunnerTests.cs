using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Internal;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Evaluation.Tests;

/// <summary>
/// The runner is the half of the harness that touches pipelines, so it is tested against fakes
/// rather than a model. The two properties worth having are that the order really rotates and that
/// a failed variant takes its whole pair out rather than half of it.
/// </summary>
public sealed class AbRunnerTests
{
    [Fact]
    public async Task RunAsync_AlternatesWhichVariantLeads()
    {
        var order = new List<string>();
        var a = new RecordingPipeline("A", order);
        var b = new RecordingPipeline("B", order);

        await AbRunner.RunAsync(
            [Variant("A", a), Variant("B", b)],
            Samples("q1", "q2", "q3", "q4"),
            TestContext.Current.CancellationToken);

        // Whichever runs second benefits from provider prompt caching and a warm store. A fixed
        // order hands one variant that advantage on every sample and reports it as a result.
        Assert.Equal(["A", "B", "B", "A", "A", "B", "B", "A"], order);
    }

    [Fact]
    public async Task RunAsync_WhenOneVariantThrows_ExcludesThePairAndContinues()
    {
        var a = SucceedingPipeline();
        var b = ThrowingOnSecondQuestionPipeline();

        var runs = await AbRunner.RunAsync(
            [Variant("A", a), Variant("B", b)],
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        // The run continues — one bad sample must not lose the other 99.
        Assert.Equal(3, runs.Count);

        // But the failed sample is not comparable, on either side.
        Assert.False(runs[1].IsComparable);
        var failure = runs[1].Failure;
        Assert.NotNull(failure);
        Assert.Equal("B", failure.VariantName);
        Assert.True(runs[0].IsComparable);
        Assert.True(runs[2].IsComparable);
    }

    [Fact]
    public async Task RunAsync_WhenOneVariantThrows_TheOtherSideIsNotLeftHalfPaired()
    {
        var runs = await AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", ThrowingOnSecondQuestionPipeline())],
            Samples("q1", "q2", "q3"),
            TestContext.Current.CancellationToken);

        // A answered q2 and its response is still here for diagnostics — but IsComparable is what
        // the statistics read, and it says this sample cannot be paired. Reporting A's surviving
        // half would average the two variants over different sample sets.
        Assert.True(runs[1].Responses.ContainsKey("A"));
        Assert.False(runs[1].Responses.ContainsKey("B"));
        Assert.False(runs[1].IsComparable);
    }

    [Fact]
    public async Task RunAsync_RecordsPerVariantLatency()
    {
        var runs = await AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
            Samples("q1"),
            TestContext.Current.CancellationToken);

        Assert.All(runs[0].Elapsed.Values, e => Assert.True(e >= TimeSpan.Zero));
        Assert.Equal(2, runs[0].Elapsed.Count);
    }

    [Fact]
    public async Task RunAsync_DoesNotTimeAVariantThatFailed()
    {
        var runs = await AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", ThrowingOnSecondQuestionPipeline())],
            Samples("q1", "q2"),
            TestContext.Current.CancellationToken);

        // How fast a call threw is not the latency being compared.
        Assert.False(runs[1].Elapsed.ContainsKey("B"));
        Assert.True(runs[1].Elapsed.ContainsKey("A"));
    }

    [Fact]
    public async Task RunAsync_KeepsResponsesKeyedByVariantNameAndSamplesInOrder()
    {
        var runs = await AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
            Samples("q1", "q2"),
            TestContext.Current.CancellationToken);

        // Part C pairs index i of one variant's report with index i of the other's, so input order
        // has to survive the run.
        Assert.Equal("q1", runs[0].Sample.Question);
        Assert.Equal("q2", runs[1].Sample.Question);
        Assert.Equal(2, runs[0].Responses.Count);
        Assert.True(runs[0].Responses.ContainsKey("A"));
        Assert.True(runs[0].Responses.ContainsKey("B"));
    }

    [Fact]
    public async Task RunAsync_WhenAPipelineReturnsNothing_TheSampleIsNotComparable()
    {
        var silent = Substitute.For<IRagPipeline>();
        silent.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RagResponse>(null!));

        var runs = await AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", silent)],
            Samples("q1"),
            TestContext.Current.CancellationToken);

        // A missing response is an absence, not an empty answer to be scored against.
        Assert.False(runs[0].IsComparable);
        var failure = runs[0].Failure;
        Assert.NotNull(failure);
        Assert.Equal("B", failure.VariantName);
        Assert.Null(failure.Exception);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
            Samples("q1"),
            cts.Token));
    }

    [Fact]
    public async Task RunAsync_CancellationIsNotRecordedAsAVariantFailure()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelling = Substitute.For<IRagPipeline>();
        cancelling.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromException<RagResponse>(new OperationCanceledException(cts.Token));
            });

        // A cancelled run is the caller stopping, not every variant going wrong on every sample.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AbRunner.RunAsync(
            [Variant("A", cancelling), Variant("B", SucceedingPipeline())],
            Samples("q1", "q2"),
            cts.Token));
    }

    [Fact]
    public async Task RunAsync_EmptyDataset_ReturnsNoRuns()
    {
        var runs = await AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
            [],
            TestContext.Current.CancellationToken);

        Assert.Empty(runs);
    }

    [Fact]
    public async Task RunAsync_FewerThanTwoVariants_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline())],
            Samples("q1"),
            TestContext.Current.CancellationToken));

    [Fact]
    public async Task RunAsync_DuplicateVariantNames_Throws()
    {
        // The report keys on the name, so two variants called "A" would silently overwrite one
        // another's responses and timings.
        await Assert.ThrowsAsync<ArgumentException>(() => AbRunner.RunAsync(
            [Variant("A", SucceedingPipeline()), Variant("A", SucceedingPipeline())],
            Samples("q1"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_BlankVariantName_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => AbRunner.RunAsync(
            [Variant("  ", SucceedingPipeline()), Variant("B", SucceedingPipeline())],
            Samples("q1"),
            TestContext.Current.CancellationToken));

    private static AbVariant Variant(string name, IRagPipeline pipeline) => new(name, pipeline);

    private static EvaluationSample[] Samples(params string[] questions)
    {
        var samples = new EvaluationSample[questions.Length];
        for (var i = 0; i < questions.Length; i++)
            samples[i] = new EvaluationSample(questions[i], PredictedAnswer: "", ReferenceAnswer: $"reference for {questions[i]}");

        return samples;
    }

    private static RagResponse Answer(string text) => new() { Answer = text, Sources = [] };

    private static IRagPipeline SucceedingPipeline()
    {
        var pipeline = Substitute.For<IRagPipeline>();
        pipeline.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Answer($"answer to {call[0]}")));

        return pipeline;
    }

    private static IRagPipeline ThrowingOnSecondQuestionPipeline()
    {
        var pipeline = Substitute.For<IRagPipeline>();
        pipeline.AskAsync(Arg.Any<string>(), Arg.Any<RagOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => string.Equals((string)call[0], "q2", StringComparison.Ordinal)
                ? Task.FromException<RagResponse>(new InvalidOperationException("the vector store went away"))
                : Task.FromResult(Answer($"answer to {call[0]}")));

        return pipeline;
    }

    /// <summary>
    /// Hand-written rather than substituted: the alternation assertion needs call order captured
    /// across two instances, which a per-substitute received-calls list cannot express.
    /// </summary>
    private sealed class RecordingPipeline(string name, List<string> order) : IRagPipeline
    {
        public Task<RagResponse> AskAsync(
            string query,
            RagOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            order.Add(name);
            return Task.FromResult(new RagResponse { Answer = $"{name} answers {query}", Sources = [] });
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
