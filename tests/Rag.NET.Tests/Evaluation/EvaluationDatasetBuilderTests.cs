using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

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
    private static List<string> SourceTexts(IReadOnlyList<EvaluationSample> samples)
    {
        var texts = new List<string>(samples.Count);
        foreach (var sample in samples)
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
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 2, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Equal(string.Empty, s.ReferenceAnswer));
        Assert.All(samples, s => Assert.NotEmpty(s.Question));
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
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 1, Mode = DatasetGenerationMode.QuestionAndAnswer },
            TestContext.Current.CancellationToken);

        Assert.Single(samples);
        Assert.NotEmpty(samples[0].ReferenceAnswer);
        Assert.NotEqual(samples[0].Question, samples[0].ReferenceAnswer);
    }

    [Fact]
    public async Task BuildAsync_WhenSampleCountIsZero_ReturnsEmpty()
    {
        var manager = MakeDataManager("Chunk A");
        var client = Substitute.For<IChatClient>();

        var builder = new EvaluationDatasetBuilder(manager, client);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 0 },
            TestContext.Current.CancellationToken);

        Assert.Empty(samples);
    }

    [Fact]
    public async Task BuildAsync_WhenLlmReturnsEmptyText_HandlesGracefully()
    {
        var manager = MakeDataManager("Chunk A");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 1, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Single(samples);
        Assert.Equal(string.Empty, samples[0].Question);
    }

    [Fact]
    public async Task BuildAsync_SampleCountExceedsChunks_ClampsToAvailable()
    {
        var manager = MakeDataManager("Only chunk");
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Q?")));

        var builder = new EvaluationDatasetBuilder(manager, client);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 100 },
            TestContext.Current.CancellationToken);

        Assert.Single(samples);
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
    public async Task BuildAsync_WithACorpusFarLargerThanTheSample_KeepsOnlyTheSample()
    {
        // 10,000 chunks in, 5 out. The old code accumulated every chunk of every document into one
        // list and sorted it by a random key to take five; this reads each document once, offers
        // its chunks to the reservoir and drops them.
        //
        // The retention bound itself — that the reservoir never holds more than SampleCount at any
        // point, not merely at the end — is asserted step by step in
        // ReservoirSamplerTests.Offer_NeverHoldsMoreThanCapacity, where the reservoir is visible.
        // It is a count, not a memory measurement, in both places.
        var fetches = new Dictionary<string, int>(StringComparer.Ordinal);
        var builder = new EvaluationDatasetBuilder(MakeCorpus(200, 50, fetches), EchoingChatClient());

        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 5, Seed = 7 },
            TestContext.Current.CancellationToken);

        Assert.Equal(5, samples.Count);
        Assert.Equal(5, SourceTexts(samples).Distinct(StringComparer.Ordinal).Count());

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
}
