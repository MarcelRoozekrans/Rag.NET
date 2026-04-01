using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

public class MindMapExtractionBehaviorTests
{
    private const string ValidJson = """
        {"title":"Root","summary":"Root summary.","children":[]}
        """;

    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();

    private static IngestionContext CreateContext(string docId = "doc-1", string chunkText = "Some chunk text.")
    {
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId(docId), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk
        {
            Text = chunkText,
            DocumentId = new DocumentId(docId),
            ChunkIndex = 0,
        });
        return ctx;
    }

    private void SetupChatClient(string response) =>
        _chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

    [Fact]
    public async Task HandleAsync_WhenExtractAtIngestionFalse_DoesNotCallLlm()
    {
        var options = new MindMapOptions { ExtractAtIngestion = false };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);
        var ctx = CreateContext();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenExtractAtIngestionTrue_CallsLlmOnce()
    {
        SetupChatClient(ValidJson);
        var options = new MindMapOptions { ExtractAtIngestion = true };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);
        var ctx = CreateContext();

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AlwaysCallsNext()
    {
        var options = new MindMapOptions { ExtractAtIngestion = false };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);
        var ctx = CreateContext();
        var nextCalled = false;

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }); });

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task HandleAsync_ConcatenatesAllChunkTexts()
    {
        SetupChatClient(ValidJson);
        var options = new MindMapOptions { ExtractAtIngestion = true };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk { Text = "First chunk.", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk { Text = "Second chunk.", DocumentId = new DocumentId("doc-1"), ChunkIndex = 1 });

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(msgs =>
                msgs.Any(m => m.Text != null &&
                              m.Text.Contains("First chunk.", StringComparison.Ordinal) &&
                              m.Text.Contains("Second chunk.", StringComparison.Ordinal))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenExtractAtIngestionTrue_AndNoChunks_DoesNotCallLlm()
    {
        var options = new MindMapOptions { ExtractAtIngestion = true };
        var extractor = new MindMapExtractor(_chatClient, graphStore: null, options);
        var sut = new MindMapExtractionBehavior(extractor, options);

        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc-1"), FileName = "test.txt" },
            GetNextBm25DocId = () => 0,
        };
        // No chunks added

        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken,
            (c, ct) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        await _chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
