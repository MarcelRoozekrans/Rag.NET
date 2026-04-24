using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class ChatAnswerEngineCompressionTests
{
    private static SearchResult MakeResult(string chunkText, string? compressed) => new()
    {
        Chunk = new TextChunk { Text = chunkText, DocumentId = new DocumentId("d"), ChunkIndex = 0 },
        Score = 0.5,
        CompressedText = compressed,
    };

    private static IChatClient CapturingChatClient(List<ChatMessage> captured)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.AddRange(ci.Arg<IEnumerable<ChatMessage>>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
            });
        return chat;
    }

    [Fact]
    public async Task AskAsync_PrefersCompressedTextWhenPresent()
    {
        var captured = new List<ChatMessage>();
        var sut = new ChatAnswerEngine(CapturingChatClient(captured));
        var sources = new List<SearchResult> { MakeResult("ORIGINAL LONG TEXT", "compressed") };

        await sut.AskAsync("q", sources, new RagOptions(), TestContext.Current.CancellationToken);

        var user = captured.Single(m => m.Role == ChatRole.User);
        Assert.Contains("compressed", user.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("ORIGINAL LONG TEXT", user.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_FallsBackToChunkTextWhenCompressedTextNull()
    {
        var captured = new List<ChatMessage>();
        var sut = new ChatAnswerEngine(CapturingChatClient(captured));
        var sources = new List<SearchResult> { MakeResult("ORIGINAL LONG TEXT", compressed: null) };

        await sut.AskAsync("q", sources, new RagOptions(), TestContext.Current.CancellationToken);

        var user = captured.Single(m => m.Role == ChatRole.User);
        Assert.Contains("ORIGINAL LONG TEXT", user.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_SkipCompressionTrue_DoesNotInvokeCompressor()
    {
        var captured = new List<ChatMessage>();
        var compressor = Substitute.For<IContextualCompressor>();
        var sut = new ChatAnswerEngine(CapturingChatClient(captured), memory: null, compressor: compressor);
        var sources = new List<SearchResult> { MakeResult("text", compressed: null) };

        await sut.AskAsync("q", sources, new RagOptions { SkipCompression = true }, TestContext.Current.CancellationToken);

        await compressor.DidNotReceive().CompressAsync(
            Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
