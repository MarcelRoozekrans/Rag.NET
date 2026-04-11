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
}
