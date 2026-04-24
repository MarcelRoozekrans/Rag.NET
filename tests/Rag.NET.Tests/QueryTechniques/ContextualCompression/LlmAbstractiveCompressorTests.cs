using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.QueryTechniques.ContextualCompression;
using Xunit;

namespace Rag.NET.Tests.QueryTechniques.ContextualCompression;

public class LlmAbstractiveCompressorTests
{
    private static SearchResult MakeResult(string text, string docId = "d", int idx = 0) =>
        new()
        {
            Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = idx },
            Score = 0.5,
        };

    private static ContextualCompressionOptions DefaultOpts() => new()
    {
        Strategy = ContextualCompressionStrategy.Abstractive,
        KeepTopSentences = null,
        MaxTokensPerChunk = 200,
    };

    [Fact]
    public async Task CompressAsync_HappyPath_StoresLlmResponseInCompressedText()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of relevant content."))));
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);

        var result = await sut.CompressAsync(
            new List<SearchResult> { MakeResult("Long original chunk with many sentences. Some are relevant. Others are not.") },
            "relevant content",
            TestContext.Current.CancellationToken);

        Assert.Equal("Summary of relevant content.", result[0].CompressedText);
    }

    [Fact]
    public async Task CompressAsync_PerChunkParallelism_RunsConcurrentlyNotSequentially()
    {
        var gate = new TaskCompletionSource<bool>();
        var arrivals = 0;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref arrivals);
                await gate.Task.ConfigureAwait(false);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            });

        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);
        var sources = Enumerable.Range(0, 5).Select(i => MakeResult($"chunk {i}", $"d{i}")).ToList();

        var compressTask = sut.CompressAsync(sources, "q", TestContext.Current.CancellationToken).AsTask();

        // Poll up to 2s for all 5 requests to fan out concurrently.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (arrivals < 5 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(5, arrivals);
        gate.SetResult(true);
        var result = await compressTask;
        Assert.All(result, r => Assert.Equal("ok", r.CompressedText));
    }

    [Fact]
    public async Task CompressAsync_OneChunkFails_OthersStillCompressed()
    {
        var call = 0;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ =>
            {
                var n = Interlocked.Increment(ref call);
                if (n == 2)
                    return Task.FromException<ChatResponse>(new InvalidOperationException("boom"));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"compressed-{n}")));
            });
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);

        var sources = Enumerable.Range(0, 3).Select(i => MakeResult($"c{i}", $"d{i}")).ToList();
        var result = await sut.CompressAsync(sources, "q", TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Count(r => r.CompressedText is null));
        Assert.Equal(2, result.Count(r => r.CompressedText is not null));
    }

    [Fact]
    public async Task CompressAsync_EmptyLlmResponse_FallsBackToNull()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))));
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);

        var result = await sut.CompressAsync(
            new List<SearchResult> { MakeResult("text") },
            "q",
            TestContext.Current.CancellationToken);

        Assert.Null(result[0].CompressedText);
    }

    [Fact]
    public async Task CompressAsync_AlreadyCompressed_SkipsWork()
    {
        var chat = Substitute.For<IChatClient>();
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);
        var source = MakeResult("text") with { CompressedText = "pre-compressed" };

        var result = await sut.CompressAsync(new List<SearchResult> { source }, "q", TestContext.Current.CancellationToken);

        Assert.Equal("pre-compressed", result[0].CompressedText);
        await chat.DidNotReceive().GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompressAsync_CancelledToken_PropagatesOCE()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => Task.FromException<ChatResponse>(new OperationCanceledException()));
        var sut = new LlmAbstractiveCompressor(chat, DefaultOpts(), NullLogger<LlmAbstractiveCompressor>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.CompressAsync(new List<SearchResult> { MakeResult("c") }, "q", cts.Token));
    }
}
