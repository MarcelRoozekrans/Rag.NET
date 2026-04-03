using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Security;
using Xunit;

namespace Rag.NET.Security.Tests;

public class PromptHardeningTests
{
    private static IReadOnlyList<SearchResult> EmptySources() => [];

    private static async IAsyncEnumerable<RagStreamingUpdate> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static PromptHardeningAnswerEngineDecorator BuildDecorator(
        IAnswerEngine inner,
        string? prefix = null)
    {
        var opts = new PromptHardeningOptions();
        if (prefix is not null)
            opts.SystemPrefix = prefix;
        return new PromptHardeningAnswerEngineDecorator(inner, opts);
    }

    [Fact]
    public async Task AskAsync_PrependsPrefixAsFirstConversationHistoryMessage()
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Do<RagOptions?>(o => captured = o),
            Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "ok", Sources = [] });

        var sut = BuildDecorator(inner);

        await sut.AskAsync("What is X?", EmptySources(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.ConversationHistory);
        Assert.True(captured.ConversationHistory!.Count >= 1);
        Assert.Equal(ChatRole.System, captured.ConversationHistory[0].Role);
        Assert.Contains("strictly as data", captured.ConversationHistory[0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_PreservesExistingHistoryAfterPrefix()
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Do<RagOptions?>(o => captured = o),
            Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "ok", Sources = [] });

        var sut = BuildDecorator(inner);
        var existingHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "prior question"),
            new(ChatRole.Assistant, "prior answer"),
        };
        var opts = new RagOptions { ConversationHistory = existingHistory };

        await sut.AskAsync("What is X?", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.ConversationHistory);
        // prefix + 2 existing = 3
        Assert.Equal(3, captured.ConversationHistory!.Count);
        Assert.Equal(ChatRole.System, captured.ConversationHistory[0].Role);
        Assert.Contains("strictly as data", captured.ConversationHistory[0].Text!, StringComparison.Ordinal);
        Assert.Equal("prior question", captured.ConversationHistory[1].Text);
        Assert.Equal("prior answer", captured.ConversationHistory[2].Text);
    }

    [Fact]
    public async Task AskAsync_EmptyPrefix_DoesNotInjectSystemMessage()
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Do<RagOptions?>(o => captured = o),
            Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "ok", Sources = [] });

        var sut = BuildDecorator(inner, prefix: "");

        await sut.AskAsync("What is X?", EmptySources(), null, TestContext.Current.CancellationToken);

        // With empty prefix, ConversationHistory should remain null (no injection)
        Assert.True(captured?.ConversationHistory is null || !captured.ConversationHistory.Any(m =>
            m.Role == ChatRole.System &&
            (m.Text?.Contains("strictly as data", StringComparison.Ordinal) == true)));
    }

    [Fact]
    public async Task AskAsync_CustomPrefix_UsesCustomText()
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Do<RagOptions?>(o => captured = o),
            Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "ok", Sources = [] });

        var sut = BuildDecorator(inner, prefix: "My custom hardening instruction.");

        await sut.AskAsync("What is X?", EmptySources(), null, TestContext.Current.CancellationToken);

        Assert.NotNull(captured?.ConversationHistory);
        Assert.Equal(ChatRole.System, captured!.ConversationHistory![0].Role);
        Assert.Contains("My custom hardening instruction.", captured.ConversationHistory[0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsync_PassesThroughOtherRagOptions()
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Do<RagOptions?>(o => captured = o),
            Arg.Any<CancellationToken>())
            .Returns(new RagResponse { Answer = "ok", Sources = [] });

        var sut = BuildDecorator(inner);
        var opts = new RagOptions { SystemPrompt = "Custom domain prompt", TopK = 10 };

        await sut.AskAsync("What is X?", EmptySources(), opts, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("Custom domain prompt", captured!.SystemPrompt);
        Assert.Equal(10, captured.TopK);
    }

    [Fact]
    public async Task AskStreamingAsync_PrependsPrefixAsFirstConversationHistoryMessage()
    {
        RagOptions? captured = null;
        var inner = Substitute.For<IAnswerEngine>();
        inner.AskStreamingAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<SearchResult>>(),
            Arg.Do<RagOptions?>(o => captured = o),
            Arg.Any<CancellationToken>())
            .Returns(EmptyAsyncEnumerable());

        var sut = BuildDecorator(inner);

        await foreach (var _ in sut.AskStreamingAsync("What is X?", EmptySources(), null, TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(captured);
        Assert.NotNull(captured!.ConversationHistory);
        Assert.Equal(ChatRole.System, captured.ConversationHistory![0].Role);
        Assert.Contains("strictly as data", captured.ConversationHistory[0].Text!, StringComparison.Ordinal);
    }
}
