using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

/// <summary>
/// Moved here from <c>Rag.NET.Tests/Evaluation/</c> in Phase 3.2. The builder is a
/// <c>Rag.NET.Evaluation</c> type, and its two Phase 3.2 collaborators —
/// <see cref="ReservoirSamplerTests"/> and <see cref="EvaluationChatCallerTests"/> — already test
/// it from here, as does the <see cref="RoutingChatClient"/> the ceiling test needs.
/// </summary>
public class EvaluationDatasetBuilderTests
{
    private static IRagDataManager MakeDataManager(params string[] chunkTexts)
    {
        var manager = Substitute.For<IRagDataManager>();
        var docId = new DocumentId("doc-1");
        var summary = new DocumentSummary
        {
            DocumentId = docId, FileName = "test.txt",
            ChunkCount = chunkTexts.Length,
            IngestedAt = DateTimeOffset.UnixEpoch,
        };
        manager.GetDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DocumentSummary> { summary });
        var chunks = chunkTexts.Select((t, i) => new TextChunk
        {
            Text = t, DocumentId = docId, ChunkIndex = i,
        }).ToList();
        manager.GetChunksAsync(docId.Value, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TextChunk>)chunks);
        return manager;
    }

    /// <summary>
    /// A corpus of <paramref name="documentCount"/> documents whose chunks are built on demand,
    /// recording how many times each document was read.
    /// </summary>
    private static IRagDataManager MakeCorpus(
        int documentCount, int chunksPerDocument, Dictionary<string, int> fetchCounts)
    {
        var manager = Substitute.For<IRagDataManager>();
        var summaries = new List<DocumentSummary>(documentCount);

        for (var d = 0; d < documentCount; d++)
        {
            var docId = new DocumentId($"doc-{d}");
            summaries.Add(new DocumentSummary
            {
                DocumentId = docId, FileName = $"doc-{d}.txt",
                ChunkCount = chunksPerDocument,
                IngestedAt = DateTimeOffset.UnixEpoch,
            });

            manager.GetChunksAsync(docId.Value, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                fetchCounts[docId.Value] = fetchCounts.GetValueOrDefault(docId.Value) + 1;
                var chunks = new List<TextChunk>(chunksPerDocument);
                for (var c = 0; c < chunksPerDocument; c++)
                {
                    chunks.Add(new TextChunk
                    {
                        Text = $"{docId.Value} chunk {c}", DocumentId = docId, ChunkIndex = c,
                    });
                }

                return (IReadOnlyList<TextChunk>)chunks;
            });
        }

        manager.GetDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentSummary>)summaries);
        return manager;
    }

    private static IChatClient EchoingChatClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ChatResponse(new ChatMessage(ChatRole.Assistant, "A question?")));
        return client;
    }

    /// <summary>The one chunk behind each sample — what the seed is supposed to pin.</summary>
    private static List<string> SourceTexts(EvaluationDataset dataset)
    {
        var texts = new List<string>(dataset.Samples.Count);
        foreach (var sample in dataset.Samples)
        {
            Assert.NotNull(sample.SourceChunks);
            texts.Add(Assert.Single(sample.SourceChunks));
        }

        return texts;
    }

    [Fact]
    public async Task BuildAsync_QuestionOnly_ReturnsSamplesWithEmptyReferenceAnswer()
    {
        var manager = MakeDataManager("Chunk A", "Chunk B", "Chunk C");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "What is chunk A about?")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 2, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, dataset.Samples.Count);
        Assert.Equal(2, dataset.Requested);
        Assert.All(dataset.Samples, s => Assert.Equal(string.Empty, s.ReferenceAnswer));
        Assert.All(dataset.Samples, s => Assert.NotEmpty(s.Question));
    }

    [Fact]
    public async Task BuildAsync_QuestionAndAnswer_ReturnsSamplesWithReferenceAnswer()
    {
        var manager = MakeDataManager("Chunk A", "Chunk B");
        var client = Substitute.For<IChatClient>();
        // First call = question, second call = answer
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "What is chunk A?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "Chunk A is about X.")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 1, Mode = DatasetGenerationMode.QuestionAndAnswer },
            TestContext.Current.CancellationToken);

        var sample = Assert.Single(dataset.Samples);
        Assert.NotEmpty(sample.ReferenceAnswer);
        Assert.NotEqual(sample.Question, sample.ReferenceAnswer, StringComparer.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_WhenSampleCountIsZero_ReturnsEmpty()
    {
        var manager = MakeDataManager("Chunk A");
        var client = Substitute.For<IChatClient>();

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 0 },
            TestContext.Current.CancellationToken);

        Assert.Empty(dataset.Samples);
        Assert.Equal(0, dataset.Requested);
        Assert.Empty(dataset.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task BuildAsync_WhenTheModelReturnsNothing_DropsTheSampleAndCountsIt(string reply)
    {
        // This test was called BuildAsync_WhenLlmReturnsEmptyText_HandlesGracefully and asserted
        //
        //     Assert.Single(samples);
        //     Assert.Equal(string.Empty, samples[0].Question);
        //
        // which certified the defect rather than the behaviour: a sample with an empty question
        // entered the dataset as valid, and every evaluator downstream then scored it — Answer
        // Relevance embeds "" and returns a cosine similarity like any other — so the corruption
        // was invisible from that point on. It is the third test written on 2026-04-11 whose name
        // promised grace and whose body swallowed a failure, after the two malformed-JSON siblings
        // fixed in Phase 3.1. Kept and re-pointed rather than deleted, so the record of what was
        // once asserted survives.
        var manager = MakeDataManager("Chunk A");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 1, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Empty(dataset.Samples);

        // Dropped, and said so: one chunk was sent, one came back unusable.
        Assert.Equal(1, dataset.Requested);
        var skip = Assert.Single(dataset.Skipped);
        Assert.Equal(EvaluationDataset.SkipReasons.EmptyQuestion, skip.Key);
        Assert.Equal(1, skip.Value);
    }

    [Fact]
    public async Task BuildAsync_WhenTheAnswerIsEmptyInQuestionAndAnswerMode_DropsTheSample()
    {
        // A question without a reference answer is not merely thin: Context Precision and Context
        // Recall both throw on an empty ReferenceAnswer, so emitting this sample would move the
        // failure to an evaluation run that cannot explain where it came from.
        var manager = MakeDataManager("Chunk A");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "What is chunk A?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "   ")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 1, Mode = DatasetGenerationMode.QuestionAndAnswer },
            TestContext.Current.CancellationToken);

        Assert.Empty(dataset.Samples);
        var skip = Assert.Single(dataset.Skipped);
        Assert.Equal(EvaluationDataset.SkipReasons.EmptyReferenceAnswer, skip.Key);
        Assert.Equal(1, skip.Value);
    }

    [Fact]
    public async Task BuildAsync_WhenOneChunkGeneratesAndOneDoesNot_ReturnsOneSampleAndOneSkip()
    {
        // The partial failure is the case that matters in practice — a whole run coming back empty
        // is noticed, a run that quietly returns four samples out of five is not.
        var manager = MakeDataManager("Chunk A", "Chunk B");
        var client = Substitute.For<IChatClient>();
        var replies = new Queue<string>(["A question?", string.Empty]);
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, Dequeue(replies))));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 2, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        var sample = Assert.Single(dataset.Samples);
        Assert.Equal("A question?", sample.Question);

        // Requested accounts for both: the samples plus the skips add up to what was sent.
        Assert.Equal(2, dataset.Requested);
        Assert.Equal(1, dataset.Skipped[EvaluationDataset.SkipReasons.EmptyQuestion]);
    }

    /// <summary>
    /// Takes the next scripted reply, thread-safely: the generations run concurrently, so the two
    /// calls can dequeue from different threads.
    /// </summary>
    private static string Dequeue(Queue<string> replies)
    {
        lock (replies)
        {
            return replies.Count > 0 ? replies.Dequeue() : string.Empty;
        }
    }

    [Fact]
    public async Task BuildAsync_SampleCountExceedsChunks_ClampsToAvailable()
    {
        var manager = MakeDataManager("Only chunk");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q?")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 100 },
            TestContext.Current.CancellationToken);

        Assert.Single(dataset.Samples);

        // Requested is what was sampled, not what was asked for: only one chunk exists to send.
        Assert.Equal(1, dataset.Requested);
    }

    [Fact]
    public async Task BuildAsync_WithAnEnormousSampleCount_StillReturnsTheWholeCorpus()
    {
        // A SampleCount far above the corpus size is a clamp, not a failure. The streaming rewrite
        // briefly made it one: the reservoir pre-allocated a List<TextChunk>(SampleCount), so
        // int.MaxValue threw OutOfMemoryException before a single document had been read, where the
        // pre-Phase-3.2 builder clamped against the corpus and returned every chunk.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new EvaluationDatasetBuilder(MakeCorpus(2, 3, fetches), EchoingChatClient());

        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = int.MaxValue, Seed = 11 },
            TestContext.Current.CancellationToken);

        Assert.Equal(6, dataset.Samples.Count);
        Assert.Equal(6, dataset.Requested);
    }

    [Fact]
    public async Task BuildAsync_SameSeed_SamplesTheSameChunks()
    {
        // The guarantee the phase exists to add. Before this, two builds over an unchanged corpus
        // drew different chunks, so a before/after measurement compared question sets as much as
        // it compared the pipeline change it was meant to measure.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new EvaluationDatasetBuilder(MakeCorpus(8, 20, fetches), EchoingChatClient());
        var options = new EvaluationDatasetBuilderOptions { SampleCount = 6, Seed = 1234 };

        var first = await builder.BuildAsync(options, TestContext.Current.CancellationToken);
        var second = await builder.BuildAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(SourceTexts(first), SourceTexts(second));
    }

    [Fact]
    public async Task BuildAsync_DifferentSeeds_GenerallySampleDifferentChunks()
    {
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new EvaluationDatasetBuilder(MakeCorpus(8, 20, fetches), EchoingChatClient());

        var a = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 6, Seed = 1 },
            TestContext.Current.CancellationToken);
        var b = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 6, Seed = 2 },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(SourceTexts(a), SourceTexts(b));
    }

    [Fact]
    public async Task BuildAsync_WithACorpusFarLargerThanTheSample_ReadsEachDocumentExactlyOnce()
    {
        // 10,000 chunks in, 5 out. The old code accumulated every chunk of every document into one
        // list and sorted it by a random key to take five; this reads each document once, offers
        // its chunks to the reservoir and drops them.
        //
        // Named for what it asserts. It was called ..._KeepsOnlyTheSample, which a build that
        // materialised the whole corpus and then took five would also pass — the assertions below
        // are a single-pass enumeration and a distinct count, neither of which observes retention.
        // The retention bound itself — that the reservoir never holds more than SampleCount at any
        // point, not merely at the end — is asserted step by step in
        // ReservoirSamplerTests.Offer_NeverHoldsMoreThanCapacity, where the reservoir is visible.
        // It is a count, not a memory measurement, in both places.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new EvaluationDatasetBuilder(MakeCorpus(200, 50, fetches), EchoingChatClient());

        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 5, Seed = 7 },
            TestContext.Current.CancellationToken);

        Assert.Equal(5, dataset.Samples.Count);
        Assert.Equal(5, SourceTexts(dataset).Distinct(StringComparer.Ordinal).Count());

        // Every document was read exactly once: nothing re-enumerates the corpus, which a
        // materialise-then-sort would have to do to stay this cheap.
        Assert.Equal(200, fetches.Count);
        Assert.All(fetches.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task BuildAsync_SameSeedAcrossADifferentCorpus_SamplesDifferentChunks()
    {
        // The documented limit, pinned rather than only asserted in prose: a seed fixes the draw
        // from what is there, and ingestion changes what is there.
        var smallFetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var largeFetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var options = new EvaluationDatasetBuilderOptions { SampleCount = 4, Seed = 99 };

        var beforeIngestion = await new EvaluationDatasetBuilder(
            MakeCorpus(2, 20, smallFetches), EchoingChatClient())
            .BuildAsync(options, TestContext.Current.CancellationToken);
        var afterIngestion = await new EvaluationDatasetBuilder(
            MakeCorpus(6, 20, largeFetches), EchoingChatClient())
            .BuildAsync(options, TestContext.Current.CancellationToken);

        Assert.NotEqual(SourceTexts(beforeIngestion), SourceTexts(afterIngestion));
    }

    [Fact]
    public async Task BuildAsync_RespectsTheConcurrencyCeiling()
    {
        // Peak observed concurrency, not a total call count: a total proves only that the work got
        // done, never that a bound held while it did. Four chunks in QuestionAndAnswer mode is
        // eight LLM calls, and the pre-3.2 builder started one chain per sampled chunk through
        // Task.WhenAll with nothing bounding them at all.
        //
        // The fan-out is deliberately observed while gated. Every generation blocks on its own
        // latch inside the fake, so an unbounded builder reaches a peak of four before anything
        // completes, while a bounded one cannot get a third call past the semaphore.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var client = new RoutingChatClient([], fallback: "A question?");
        client.GateCalls();
        var builder = new EvaluationDatasetBuilder(MakeCorpus(1, 4, fetches), client);

        var pending = builder.BuildAsync(
            new EvaluationDatasetBuilderOptions
            {
                SampleCount = 4,
                Seed = 3,
                Mode = DatasetGenerationMode.QuestionAndAnswer,
                MaxConcurrentCalls = 2,
            },
            TestContext.Current.CancellationToken);

        await WaitForAsync(() => client.CallCount >= 2, TestContext.Current.CancellationToken);
        var peakWhileGated = client.PeakInFlight;
        client.ReleaseAll();
        var dataset = await pending;

        Assert.Equal(4, dataset.Samples.Count);

        // Two calls per sample, and never more than two of them in flight.
        Assert.Equal(8, client.CallCount);
        Assert.True(
            client.PeakInFlight <= 2,
            $"peak was {client.PeakInFlight} ({peakWhileGated} while gated), ceiling was 2");
    }

    [Fact]
    public async Task BuildAsync_WhenTheCeilingIsNotPositive_Throws()
    {
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new EvaluationDatasetBuilder(MakeCorpus(1, 2, fetches), EchoingChatClient());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => builder.BuildAsync(
                new EvaluationDatasetBuilderOptions { SampleCount = 2, MaxConcurrentCalls = 0 },
                TestContext.Current.CancellationToken));

        Assert.Equal("options", exception.ParamName);

        // Rejected before the corpus is read: a nonsensical ceiling should not cost an enumeration
        // of every document first.
        Assert.Empty(fetches);
    }

    [Fact]
    public async Task BuildAsync_WithUsageAndPrices_BillsEveryCall()
    {
        // The builder is pure LLM spend — one or two calls per sample — and recorded none of it
        // before this phase, while the RAGAS metrics had been billing since 3.1.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "A question?")
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 },
        };
        var builder = new EvaluationDatasetBuilder(MakeCorpus(1, 3, fetches), client, ledger);

        await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions
            {
                SampleCount = 3,
                Seed = 5,
                PricePerInputToken = 0.001m,
                PricePerOutputToken = 0.002m,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, ledger.Entries.Count);
        Assert.All(ledger.Entries, entry =>
        {
            Assert.Equal(CostKind.Chat, entry.Kind);
            Assert.Equal((100 * 0.001m) + (10 * 0.002m), entry.Cost);
        });
    }

    [Fact]
    public async Task BuildAsync_WhenTheModelReportsNoUsage_BillsNothing()
    {
        // Recording a zero-token entry would state as fact that the build was free. The guard lives
        // in the shared caller; this pins that the builder goes through it rather than around it.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "A question?") { Usage = null };
        var builder = new EvaluationDatasetBuilder(MakeCorpus(1, 3, fetches), client, ledger);

        await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions
            {
                SampleCount = 3, Seed = 5, PricePerInputToken = 1m, PricePerOutputToken = 1m,
            },
            TestContext.Current.CancellationToken);

        Assert.Empty(ledger.Entries);
    }

    /// <summary>
    /// Spins until <paramref name="condition"/> holds, or five seconds pass.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. The condition is satisfied on the first check today — the fan-out runs
    /// synchronously up to each call's latch — but an unbounded spin would turn any future
    /// regression that stops the builder starting its calls into a hung run rather than a red one.
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
