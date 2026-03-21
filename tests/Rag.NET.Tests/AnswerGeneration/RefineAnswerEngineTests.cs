using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class RefineAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly RefineAnswerEngine _sut;

    public RefineAnswerEngineTests()
    {
        _sut = new RefineAnswerEngine(_chatClient, NullLogger<RefineAnswerEngine>.Instance);
    }

    private static SearchResult MakeSource(string text, string docId = "doc-1") =>
        new() { Chunk = new TextChunk { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = 0 }, Score = 0.9 };

    private static ChatResponse ChatReply(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    [Fact]
    public async Task AskAsync_ThreeSources_InitialPlusTwoRefines()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
            MakeSource("chunk C", "doc-3"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined once"), ChatReply("final answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(3).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("final answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_OneSource_OnlyInitialCall()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("only answer"));

        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        Assert.Equal("only answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_RefinementCallThrows_PreviousAnswerPreserved()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                x => ChatReply("initial answer"),
                x => throw new InvalidOperationException("LLM error"));

        // Should not throw — preserve previous answer
        var result = await _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("initial answer", result.Answer);
    }

    [Fact]
    public async Task AskAsync_InitialCallThrows_PropagatesException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new InvalidOperationException("LLM error"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AskAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var sources = new List<SearchResult> { MakeSource("chunk A") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<ChatResponse>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.AskAsync("What?", sources, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenSingleTextDelta()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined"));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("refined", updates[1].TextDelta);
        Assert.Null(updates[1].Sources);
    }

    [Fact]
    public async Task AskAsync_WithCustomPromptTemplates_UsesCustomTemplates()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };
        var opts = new RagOptions
        {
            RefineOptions = new RefineOptions
            {
                InitialPromptTemplate = "Initial: {chunk} Q: {query}",
                RefinePromptTemplate = "Refine: {answer} + {chunk} Q: {query}",
            }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined"));

        var result = await _sut.AskAsync("my question", sources, opts, TestContext.Current.CancellationToken);

        Assert.Equal("refined", result.Answer);

        // Verify initial template was used
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Initial:"))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // Verify refine template was used
        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Refine:"))),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithSystemPrompt_IncludesItInMessages()
    {
        var sources = new List<SearchResult>
        {
            MakeSource("chunk A", "doc-1"),
            MakeSource("chunk B", "doc-2"),
        };
        var opts = new RagOptions { SystemPrompt = "You are a helpful assistant." };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ChatReply("initial"), ChatReply("refined"));

        await _sut.AskAsync("What?", sources, opts, TestContext.Current.CancellationToken);

        // Both initial and refine calls should include the system message
        await _chatClient.Received(2).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Role == ChatRole.System && m.Text == "You are a helpful assistant.")),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
