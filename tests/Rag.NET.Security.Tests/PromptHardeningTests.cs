using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class PromptHardeningTests
{
    private static IReadOnlyList<SearchResult> EmptySources() => [];

    [Fact]
    public async Task AskAsync_WithHardeningPrefix_SystemMessagePrepended()
    {
        IList<ChatMessage>? capturedMessages = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => capturedMessages = m),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new ChatAnswerEngine(client);
        var opts = new RagOptions { PromptHardeningPrefix = PromptHardeningOptions.DefaultSystemPrefix };

        await sut.AskAsync("What is X?", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedMessages);
        Assert.Contains(capturedMessages, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("strictly as data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskAsync_WithHardeningPrefix_SystemMessageIsFirst()
    {
        IList<ChatMessage>? capturedMessages = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => capturedMessages = m),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new ChatAnswerEngine(client);
        var opts = new RagOptions { PromptHardeningPrefix = PromptHardeningOptions.DefaultSystemPrefix };

        await sut.AskAsync("What is X?", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedMessages);
        Assert.True(capturedMessages!.Count >= 1);
        Assert.Equal(ChatRole.System, capturedMessages[0].Role);
        Assert.Contains("strictly as data", capturedMessages[0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_WithoutHardeningPrefix_NoHardeningSystemMessage()
    {
        IList<ChatMessage>? capturedMessages = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => capturedMessages = m),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new ChatAnswerEngine(client);
        // No PromptHardeningPrefix set
        await sut.AskAsync("What is X?", EmptySources(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedMessages);
        Assert.DoesNotContain(capturedMessages, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("strictly as data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskAsync_WithEmptyHardeningPrefix_NoHardeningSystemMessage()
    {
        IList<ChatMessage>? capturedMessages = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => capturedMessages = m),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new ChatAnswerEngine(client);
        var opts = new RagOptions { PromptHardeningPrefix = "" };

        await sut.AskAsync("What is X?", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedMessages);
        // With empty prefix the hardening system message should NOT appear; the normal system message still exists
        Assert.DoesNotContain(capturedMessages, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("strictly as data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskAsync_WithHardeningPrefix_NormalSystemPromptStillPresent()
    {
        IList<ChatMessage>? capturedMessages = null;
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
            Arg.Do<IList<ChatMessage>>(m => capturedMessages = m),
            Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]));

        var sut = new ChatAnswerEngine(client);
        var opts = new RagOptions
        {
            PromptHardeningPrefix = PromptHardeningOptions.DefaultSystemPrefix,
            SystemPrompt = "Custom domain prompt",
        };

        await sut.AskAsync("What is X?", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedMessages);
        Assert.Contains(capturedMessages, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("strictly as data", StringComparison.Ordinal));
        Assert.Contains(capturedMessages, m =>
            m.Role == ChatRole.System &&
            m.Text!.Contains("Custom domain prompt", StringComparison.Ordinal));
    }
}
