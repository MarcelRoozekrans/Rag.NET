using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Memory;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Memory;

public class ConversationMemoryTests
{
    [Fact]
    public void Options_Defaults_AreCorrect()
    {
        var opts = new ConversationMemoryOptions();
        Assert.Null(opts.MaxExchanges);
        Assert.Null(opts.MaxTokens);
        Assert.False(opts.UseSummary);
        Assert.Null(opts.SummaryPromptTemplate);
    }

    private static List<ChatMessage> MakeExchanges(int count)
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < count; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"Question {i + 1}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"Answer {i + 1}"));
        }
        return messages;
    }

    [Fact]
    public async Task SlidingWindow_KeepsLastNExchanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var opts = new ConversationMemoryOptions { MaxExchanges = 2 };
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);
        var history = MakeExchanges(5);

        var result = await sut.ProcessAsync(history, ct);

        Assert.Equal(4, result.Count); // 2 exchanges = 4 messages
        Assert.True(result[0].Text?.Contains("Question 4", StringComparison.Ordinal));
        Assert.True(result[^1].Text?.Contains("Answer 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlidingWindow_PreservesSystemMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var opts = new ConversationMemoryOptions { MaxExchanges = 1 };
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.User, "Q1"), new(ChatRole.Assistant, "A1"),
            new(ChatRole.User, "Q2"), new(ChatRole.Assistant, "A2"),
        };

        var result = await sut.ProcessAsync(history, ct);

        Assert.Equal(3, result.Count); // system + last exchange
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.True(result[1].Text?.Contains("Q2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoStrategiesConfigured_ReturnsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var opts = new ConversationMemoryOptions();
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);
        var history = MakeExchanges(3);

        var result = await sut.ProcessAsync(history, ct);

        Assert.Equal(history.Count, result.Count);
    }

    [Fact]
    public async Task EmptyHistory_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var opts = new ConversationMemoryOptions { MaxExchanges = 2 };
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);

        var result = await sut.ProcessAsync([], ct);

        Assert.Empty(result);
    }

    [Fact]
    public async Task TokenBudget_TrimsOldestNonSystemMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var opts = new ConversationMemoryOptions { MaxTokens = 15 };
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);
        var history = MakeExchanges(5);

        var result = await sut.ProcessAsync(history, ct);

        Assert.True(result.Count < history.Count);
        Assert.True(result.Count > 0);
        Assert.True(result[^1].Text?.Contains("Answer 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TokenBudget_PreservesSystemMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var opts = new ConversationMemoryOptions { MaxTokens = 10 };
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "System prompt."),
            new(ChatRole.User, "Q1"), new(ChatRole.Assistant, "A1"),
            new(ChatRole.User, "Q2"), new(ChatRole.Assistant, "A2"),
        };

        var result = await sut.ProcessAsync(history, ct);

        Assert.Contains(result, m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task WindowAndBudget_Combined_AppliesWindowFirstThenBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        // Window keeps 3 exchanges (6 messages), then budget trims further
        var opts = new ConversationMemoryOptions { MaxExchanges = 3, MaxTokens = 10 };
        var sut = new ConversationMemoryPipeline(opts, chatClient: null);
        var history = MakeExchanges(5);

        var result = await sut.ProcessAsync(history, ct);

        // Should be fewer than 6 (window) and fewer than original 10
        Assert.True(result.Count < 6);
        Assert.True(result[^1].Text?.Contains("Answer 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_WhenTrimmedMessages_PrependsSummarySystemMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of old conversation.")));

        var opts = new ConversationMemoryOptions { MaxExchanges = 1, UseSummary = true };
        var sut = new ConversationMemoryPipeline(opts, chatClient);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Q1"), new(ChatRole.Assistant, "A1"),
            new(ChatRole.User, "Q2"), new(ChatRole.Assistant, "A2"),
        };

        var result = await sut.ProcessAsync(history, ct);

        // Should have: summary system message + last exchange (Q2, A2) = 3 messages
        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.System, result[0].Role);
        Assert.True(result[0].Text!.Contains("Summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_LlmFails_ReturnsTrimmedWithoutSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM down"));

        var opts = new ConversationMemoryOptions { MaxExchanges = 1, UseSummary = true };
        var sut = new ConversationMemoryPipeline(opts, chatClient);
        var history = MakeExchanges(3);

        var result = await sut.ProcessAsync(history, ct);

        // Should still work — just no summary prepended
        Assert.Equal(2, result.Count); // last exchange only, no summary
    }

    [Fact]
    public async Task Summary_NoTrimmedMessages_SkipsSummaryCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();

        var opts = new ConversationMemoryOptions { MaxExchanges = 10, UseSummary = true };
        var sut = new ConversationMemoryPipeline(opts, chatClient);
        var history = MakeExchanges(2); // well within window

        var result = await sut.ProcessAsync(history, ct);

        await chatClient.DidNotReceive().GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_CustomPromptTemplate_UsedInLlmCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Custom summary.")));

        var opts = new ConversationMemoryOptions
        {
            MaxExchanges = 1,
            UseSummary = true,
            SummaryPromptTemplate = "Summarize this chat: {messages}",
        };
        var sut = new ConversationMemoryPipeline(opts, chatClient);
        var history = MakeExchanges(3);

        _ = await sut.ProcessAsync(history, ct);

        await chatClient.Received(1).GetResponseAsync(
            Arg.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Summarize this chat", StringComparison.Ordinal))),
            Arg.Any<ChatOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Summary_OperationCanceled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var chatClient = Substitute.For<IChatClient>();
        var opts = new ConversationMemoryOptions { MaxExchanges = 1, UseSummary = true };
        var sut = new ConversationMemoryPipeline(opts, chatClient);
        var history = MakeExchanges(3);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ProcessAsync(history, cts.Token));
    }
}
