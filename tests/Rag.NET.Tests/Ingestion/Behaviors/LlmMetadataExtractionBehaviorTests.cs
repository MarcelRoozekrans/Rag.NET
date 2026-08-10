using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion.Behaviors;

public class LlmMetadataExtractionBehaviorTests
{
    private static IngestionContext MakeContext(params TextChunk[] chunks)
    {
        var ctx = new IngestionContext
        {
            Stream = new MemoryStream(),
            Metadata = new DocumentMetadata
            {
                DocumentId = new DocumentId("doc-1"),
                FileName = "test.txt",
            },
            GetNextBm25DocId = () => 1,
        };
        ctx.Chunks.AddRange(chunks);
        return ctx;
    }

    private static TextChunk MakeChunk(string text, int index = 0) =>
        new() { Text = text, DocumentId = new DocumentId("doc-1"), ChunkIndex = index };

    private static ValueTask<IngestionResult> StubNext(IngestionContext ctx, CancellationToken _) =>
        ValueTask.FromResult(new IngestionResult { DocumentId = ctx.Metadata.DocumentId, ChunksStored = ctx.Chunks.Count });

    // ── No-op when options not set ────────────────────────────────────────────

    [Fact]
    public async Task WhenOptionsNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        var sut = new LlmMetadataExtractionBehavior { ChatClient = chatClient, ExtractionOptions = null };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenChatClientNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = null,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsValidJson_MergesTagsIntoChunkMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, """{"topic":"security","year":"2024"}""")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some security document");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Equal<MetadataValue>("security", chunk.Metadata["topic"]);
        Assert.Equal<MetadataValue>("2024", chunk.Metadata["year"]);
    }

    private const string TagPayload = """{"topic":"security","year":"2024"}""";

    public static TheoryData<string, string> WrappedResponseShapes() => new()
    {
        { "preamble then unlabelled fence", $"Here is the extracted metadata in JSON format:\n\n```\n{TagPayload}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{TagPayload}\n```" },
        { "preamble then bare json", $"Here is the JSON:\n\n{TagPayload}" },
        { "labelled fence only", $"```json\n{TagPayload}\n```" },
        { "fence then trailing prose", $"```\n{TagPayload}\n```\n\nLet me know if you need anything else." },
    };

    [Theory]
    [MemberData(nameof(WrappedResponseShapes))]
    public async Task WhenLlmWrapsTheJson_TagsStillLand(string shape, string response)
    {
        // This site had no fence handling: every one of these shapes failed to deserialize and
        // left every chunk untagged, with a per-chunk warning as the only trace.
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some security document");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.True(
            chunk.Metadata.ContainsKey("topic"),
            $"The '{shape}' response produced no tags. The JSON inside it is valid, so parsing " +
            "failed to find it and the chunk silently stayed untagged.");
        Assert.Equal<MetadataValue>("security", chunk.Metadata["topic"]);
        Assert.Equal<MetadataValue>("2024", chunk.Metadata["year"]);
    }

    // ── Schema-guided: unknown keys ignored ───────────────────────────────────

    [Fact]
    public async Task WhenSchemaProvided_IgnoresKeysNotInSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, """{"topic":"security","unknown_key":"should-be-ignored"}""")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions
            {
                Schema = [new AttributeInfo("topic", "Main subject area")]
            }
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.True(chunk.Metadata.ContainsKey("topic"));
        Assert.False(chunk.Metadata.ContainsKey("unknown_key"));
    }

    // ── Invalid JSON ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsInvalidJson_ChunkMetadataUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "not json at all")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        // Should not throw
        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
    }

    // ── next is always called ─────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmFails_NextIsStillCalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "not json")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        var nextCalled = false;
        ValueTask<IngestionResult> TrackingNext(IngestionContext c, CancellationToken _)
        {
            nextCalled = true;
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 });
        }

        await sut.HandleAsync(ctx, ct, TrackingNext);

        Assert.True(nextCalled);
    }

    // ── Empty JSON ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsEmptyJson_NoTagsAdded()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "{}")]));

        var sut = new LlmMetadataExtractionBehavior
        {
            ChatClient = chatClient,
            ExtractionOptions = new LlmMetadataExtractionOptions()
        };
        var chunk = MakeChunk("some text");
        var ctx = MakeContext(chunk);

        await sut.HandleAsync(ctx, ct, StubNext);

        Assert.Empty(chunk.Metadata);
    }
}
