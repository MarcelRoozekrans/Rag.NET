using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.SelfQuery;
using Xunit;

namespace Rag.NET.Tests.Retrieval.Behaviors;

public class SelfQueryBehaviorTests
{
    private static RetrievalContext MakeCtx(RetrievalOptions? options = null) =>
        new() { Query = "show me 2024 security documents", Options = options ?? new RetrievalOptions() };

    private static (Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next, Func<RetrievalContext?> getCapture)
        CapturingNext()
    {
        RetrievalContext? captured = null;
        return (
            (ctx, _) => { captured = ctx; return ValueTask.FromResult<IReadOnlyList<SearchResult>>([]); },
            () => captured
        );
    }

    // ── No-op paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenOptionsNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = null };
        var ctx = MakeCtx();
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, getCapture());
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenChatClientNull_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new SelfQueryBehavior { ChatClient = null, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, getCapture());
    }

    [Fact]
    public async Task WhenUseSelfQueryFalse_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx(new RetrievalOptions { UseSelfQuery = false });
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        Assert.Same(ctx, getCapture());
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsQueryAndFilters_SetsEmbeddingOverrideAndFilter()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant,
                """{"query":"security documents","filters":[{"key":"year","value":"2024"},{"key":"topic","value":"security"}]}""")]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        var captured = getCapture()!;
        Assert.Equal("security documents", captured.Options.EmbeddingTextOverride);
        Assert.NotNull(captured.Options.Filter);
    }

    // ── Empty filters ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsNoFilters_SetsQueryOverrideOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant,
                """{"query":"refined query","filters":[]}""")]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        var captured = getCapture()!;
        Assert.Equal("refined query", captured.Options.EmbeddingTextOverride);
        Assert.Null(captured.Options.Filter);
    }

    // ── Invalid JSON ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLlmReturnsInvalidJson_OriginalContextPassedToNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, "not json")]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        // next must still be called (pipeline must not abort)
        Assert.NotNull(getCapture());
        // no filter, no embedding override — original query preserved
        Assert.Null(getCapture()!.Options.Filter);
        Assert.Null(getCapture()!.Options.EmbeddingTextOverride);
    }
}
