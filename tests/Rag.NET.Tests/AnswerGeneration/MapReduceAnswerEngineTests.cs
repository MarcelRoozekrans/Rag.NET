using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class MapReduceAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly MapReduceAnswerEngine _sut;

    public MapReduceAnswerEngineTests()
    {
        _sut = new MapReduceAnswerEngine(_chatClient, NullLogger<MapReduceAnswerEngine>.Instance);
    }

    private static SearchResult MakeSource(string text, string docId = "doc-1") =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = 0.9 };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public async Task AskAsync_ThreeSources_MapsEachThenReduces()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
            MakeSource("chunk C", "doc-3"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("partial"), ChatReply("partial"), ChatReply("final answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(4).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_OneSourceReturnsNotFound_FilteredBeforeReduce()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        // First map returns "not found", second returns a partial answer
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("not found"), ChatReply("partial answer"), ChatReply("final"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        // 2 map calls + 1 reduce = 3 total
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final", result.Answer);
    }

    [Fact]
    public async Task AskAsync_AllSourcesReturnNotFound_ReduceStillCalled()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A"),
            MakeSource("chunk B"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("not found"), ChatReply("  NOT FOUND  "), ChatReply("no information available"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        // 2 map + 1 reduce = 3 calls; reduce receives empty partials
        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("no information available", result.Answer);
    }

    [Fact]
    public async Task AskAsync_MapCallThrows_ChunkSkippedAndWarningLogged()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                x => throw new InvalidOperationException("LLM error"),
                x => ChatReply("partial answer"),
                x => ChatReply("final answer"));

        // Should not throw — failed chunk treated as "not found"
        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("final answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(x => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenSingleTextDelta()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("map answer"), ChatReply("final answer"));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("final answer", updates[1].TextDelta);
        Assert.Null(updates[1].Sources);
    }

    [Fact]
    public async Task AskAsync_WithSystemPrompt_IncludesItInMessages()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        var opts = new RagOptions { SystemPrompt = "You are a helpful assistant." };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final"));

        await _sut.AskAsync("What?", sources, opts, TestContext.Current.CancellationToken);

        // Both map call and reduce call should include the system message
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m => m.Role == ChatRole.System && m.Text == "You are a helpful assistant.")),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithCustomPromptTemplates_UsesCustomTemplates()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        var opts = new RagOptions
        {
            MapReduceOptions = new MapReduceOptions
            {
                MapPromptTemplate = "Custom map: {chunk} Q: {query}",
                ReducePromptTemplate = "Custom reduce: {partials} Q: {query}",
            }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("partial"), ChatReply("final"));

        var result = await _sut.AskAsync("my question", sources, opts, TestContext.Current.CancellationToken);

        Assert.Equal("final", result.Answer);

        // Verify the map call used the custom map template
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m => m.Text != null && m.Text.Contains("Custom map:"))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // Verify the reduce call used the custom reduce template
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs!.Any(m => m.Text != null && m.Text.Contains("Custom reduce:"))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
