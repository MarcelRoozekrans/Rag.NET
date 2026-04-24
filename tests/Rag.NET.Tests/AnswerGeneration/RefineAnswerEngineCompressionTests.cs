using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class RefineAnswerEngineCompressionTests
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
                captured.AddRange(ci.Arg<IEnumerable<ChatMessage>>());
                var text = queue.Count > 0 ? queue.Dequeue() : "answer";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
            });
        return chat;
    }

    [Fact]
    public async Task AskAsync_WithCompressor_PrefersCompressedText()
    {
        var captured = new List<ChatMessage>();
        var chat = CapturingChatClient(captured, "initial answer", "refined answer");

        var compressor = Substitute.For<IContextualCompressor>();
#pragma warning disable EPS06
        compressor.CompressAsync(Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var input = ci.Arg<IReadOnlyList<SearchResult>>();
                var compressed = input
                    .Select(s => s with { CompressedText = "COMPRESSED" })
                    .ToArray();
                return new ValueTask<IReadOnlyList<SearchResult>>(compressed);
            });
#pragma warning restore EPS06

        var sut = new RefineAnswerEngine(chat, NullLogger<RefineAnswerEngine>.Instance, memory: null, compressor: compressor);
        var sources = new List<SearchResult>
        {
            MakeResult("ORIGINAL A"),
            MakeResult("ORIGINAL B"),
        };

        await sut.AskAsync("q", sources, new RagOptions(), TestContext.Current.CancellationToken);

        // Every user message must reference the compressed text, not the original chunk text.
        var userMessages = captured.Where(m => m.Role == ChatRole.User).ToList();
        Assert.NotEmpty(userMessages);
        foreach (var msg in userMessages)
        {
            Assert.Contains("COMPRESSED", msg.Text!, StringComparison.Ordinal);
            Assert.DoesNotContain("ORIGINAL A", msg.Text!, StringComparison.Ordinal);
            Assert.DoesNotContain("ORIGINAL B", msg.Text!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AskAsync_SkipCompressionTrue_DoesNotInvokeCompressor()
    {
        var captured = new List<ChatMessage>();
        var chat = CapturingChatClient(captured, "initial", "refined");
        var compressor = Substitute.For<IContextualCompressor>();
        var sut = new RefineAnswerEngine(chat, NullLogger<RefineAnswerEngine>.Instance, memory: null, compressor: compressor);
        var sources = new List<SearchResult>
        {
            MakeResult("a"),
            MakeResult("b"),
        };

        await sut.AskAsync("q", sources, new RagOptions { SkipCompression = true }, TestContext.Current.CancellationToken);

        await compressor.DidNotReceive().CompressAsync(
            Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
