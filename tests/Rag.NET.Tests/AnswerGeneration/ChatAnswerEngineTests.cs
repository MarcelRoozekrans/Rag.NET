using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.AnswerGeneration;

public class ChatAnswerEngineTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly ChatAnswerEngine _sut;

    public ChatAnswerEngineTests()
    {
        _sut = new ChatAnswerEngine(_chatClient);
    }

    [Fact]
    public async Task AskAsync_BuildsPromptAndReturnsResponse()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "Source text", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 }, Score = 0.9 }
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The answer")));

        var result = await _sut.AskAsync("What is this?", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("The answer", result.Answer);
        Assert.Same(sources, result.Sources);
    }

    [Fact]
    public async Task AskAsync_WithCustomSystemPrompt_UsesIt()
    {
        var sources = new List<SearchResult>();
        var opts = new RagOptions { SystemPrompt = "Custom prompt" };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await _sut.AskAsync("q", sources, opts, TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs!.Any(m => m.Role == ChatRole.System && m.Text == "Custom prompt")),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithHistorySystemMessageAndCustomSystemPrompt_OrdersHistorySystemFirst()
    {
        var sources = new List<SearchResult>();
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "Prompt-hardening prefix"),
            new(ChatRole.User, "previous question"),
            new(ChatRole.Assistant, "previous answer"),
        };
        var opts = new RagOptions { SystemPrompt = "Custom prompt", ConversationHistory = history };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await _sut.AskAsync("q", sources, opts, TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs!.Count == 5 &&
                msgs[0].Role == ChatRole.System && msgs[0].Text == "Prompt-hardening prefix" &&
                msgs[1].Role == ChatRole.System && msgs[1].Text == "Custom prompt" &&
                msgs[2].Role == ChatRole.User && msgs[2].Text == "previous question" &&
                msgs[3].Role == ChatRole.Assistant && msgs[3].Text == "previous answer" &&
                msgs[4].Role == ChatRole.User && msgs[4].Text!.StartsWith("Context:", StringComparison.Ordinal) &&
                msgs[4].Text!.Contains("Question: q", StringComparison.Ordinal)),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithTemperature_PassesItToChatOptions()
    {
        var sources = new List<SearchResult>();
        var opts = new RagOptions { Temperature = 0.7f };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await _sut.AskAsync("q", sources, opts, TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Is<ChatOptions?>(o => o != null && o.Temperature == 0.7f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskAsync_WithConversationHistory_IncludesItInMessages()
    {
        var sources = new List<SearchResult>();
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "previous question"),
            new(ChatRole.Assistant, "previous answer"),
        };
        var opts = new RagOptions { ConversationHistory = history };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await _sut.AskAsync("q", sources, opts, TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs!.Count == 4 && // system + 2 history + user
                msgs[1].Text == "previous question" &&
                msgs[2].Text == "previous answer"),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsSourcesThenTextDeltas()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "Source text", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 }, Score = 0.9 }
        };

        _chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new ChatResponseUpdate { Contents = [new TextContent("Hello")] },
                new ChatResponseUpdate { Contents = [new TextContent(" world")] }));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("Hello", updates[1].TextDelta);
        Assert.Equal(" world", updates[2].TextDelta);
    }

    [Fact]
    public async Task AskAsync_WhenResponseTextIsNull_ReturnsEmptyString()
    {
        var sources = new List<SearchResult>();
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, (string?)null)));

        var result = await _sut.AskAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Answer);
    }

    [Fact]
    public async Task AskStreamingAsync_WithMultipleUpdates_YieldsSourcesThenAllTextDeltas()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "Source text", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 }, Score = 0.9 }
        };

        _chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(
                new ChatResponseUpdate { Contents = [new TextContent("Hello ")] },
                new ChatResponseUpdate { Contents = [new TextContent("world")] }));

        var updates = new List<RagStreamingUpdate>();
        await foreach (var update in _sut.AskStreamingAsync("q", sources, cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        Assert.Same(sources, updates[0].Sources);
        Assert.Null(updates[0].TextDelta);
        Assert.Equal("Hello ", updates[1].TextDelta);
        Assert.Equal("world", updates[2].TextDelta);
    }

    [Fact]
    public async Task AskAsync_IncludesSourceTextInUserMessage()
    {
        var sources = new List<SearchResult>
        {
            new() { Chunk = new TextChunk { Text = "First source", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 }, Score = 0.9 },
            new() { Chunk = new TextChunk { Text = "Second source", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0 }, Score = 0.8 },
        };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await _sut.AskAsync("my question", sources, cancellationToken: TestContext.Current.CancellationToken);

        await _chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs =>
                msgs![msgs.Count - 1].Text!.Contains("First source") &&
                msgs[msgs.Count - 1].Text!.Contains("Second source") &&
                msgs[msgs.Count - 1].Text!.Contains("my question")),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(
        params ChatResponseUpdate[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}
