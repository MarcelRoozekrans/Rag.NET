using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class MapReduceAnswerEngineCompressionTests
{
    private static SearchResult MakeResult(string chunkText, string? compressed = null) => new()
    {
        Chunk = new TextChunk { Text = chunkText, DocumentId = new DocumentId("d"), ChunkIndex = 0 },
        Score = 0.5,
        CompressedText = compressed,
    };

    private static IChatClient CapturingChatClient(List<ChatMessage> captured, params string[] replies)
    {
        var chat = Substitute.For<IChatClient>();
        var queue = new Queue<string>(replies);
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.AddRange(ci.Arg<IEnumerable<ChatMessage>>()!);
                var text = queue.Count > 0 ? queue.Dequeue() : "answer";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            });
        return chat;
    }

    [Fact]
    public async Task AskAsync_WithCompressor_PrefersCompressedText()
    {
        var captured = new List<ChatMessage>();
        var chat = CapturingChatClient(captured, "partial", "final");

        // Compressor replaces each source with one carrying CompressedText set.
        var compressor = Substitute.For<IContextualCompressor>();
#pragma warning disable EPS06
        compressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var input = ci.Arg<IReadOnlyList<SearchResult>>();
                var compressed = input!
                    .Select(s => s with { CompressedText = "COMPRESSED" })
                    .ToArray();
                return new ValueTask<IReadOnlyList<SearchResult>>(compressed);
            });
#pragma warning restore EPS06

        var sut = new MapReduceAnswerEngine(chat, NullLogger<MapReduceAnswerEngine>.Instance, memory: null, compressor: compressor);
        var sources = new List<SearchResult> { MakeResult("ORIGINAL LONG TEXT") };

        await sut.AskAsync("q", sources, new RagOptions(), TestContext.Current.CancellationToken);

        // The map-call user message must contain the compressed text, not the original chunk text.
        var mapMessage = captured.First(m => m.Role == ChatRole.User && m.Text is not null && m.Text.Contains("COMPRESSED", StringComparison.Ordinal));
        Assert.NotNull(mapMessage);
        Assert.DoesNotContain("ORIGINAL LONG TEXT", mapMessage.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_SkipCompressionTrue_DoesNotInvokeCompressor()
    {
        var captured = new List<ChatMessage>();
        var chat = CapturingChatClient(captured, "partial", "final");
        var compressor = Substitute.For<IContextualCompressor>();
        var sut = new MapReduceAnswerEngine(chat, NullLogger<MapReduceAnswerEngine>.Instance, memory: null, compressor: compressor);
        var sources = new List<SearchResult> { MakeResult("text") };

        await sut.AskAsync("q", sources, new RagOptions { SkipCompression = true }, TestContext.Current.CancellationToken);

        await compressor.DidNotReceive().CompressAsync(
            Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
