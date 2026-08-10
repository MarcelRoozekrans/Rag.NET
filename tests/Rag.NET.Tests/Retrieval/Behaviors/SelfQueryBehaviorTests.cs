using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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

    private const string ParsePayload =
        """{"query":"security documents","filters":[{"key":"year","value":"2024"}]}""";

    public static TheoryData<string, string> WrappedResponseShapes() => new()
    {
        { "preamble then unlabelled fence", $"Here is the parsed query in JSON format:\n\n```\n{ParsePayload}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{ParsePayload}\n```" },
        { "preamble then bare json", $"Here is the JSON:\n\n{ParsePayload}" },
        { "labelled fence only", $"```json\n{ParsePayload}\n```" },
        { "fence then trailing prose", $"```\n{ParsePayload}\n```\n\nLet me know if you need anything else." },
    };

    [Theory]
    [MemberData(nameof(WrappedResponseShapes))]
    public async Task WhenLlmWrapsTheJson_SelfQueryStillApplies(string shape, string response)
    {
        // This site had no fence handling: every one of these shapes threw in JsonDocument.Parse
        // and quietly turned self-query off for the request, with one warning as the only trace.
        var ct = TestContext.Current.CancellationToken;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));

        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx();
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        var captured = getCapture()!;
        Assert.True(
            string.Equals("security documents", captured.Options.EmbeddingTextOverride, StringComparison.Ordinal),
            $"The '{shape}' response did not apply self-query. The JSON inside it is valid, so " +
            "parsing failed to find it and the behavior silently fell back to the raw query.");
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

        var fakeLogger = new FakeLogger();
        var sut = new SelfQueryBehavior { ChatClient = chatClient, SelfQueryOptions = new SelfQueryOptions() };
        var ctx = MakeCtx() with { Logger = fakeLogger };
        var (next, getCapture) = CapturingNext();

        await sut.HandleAsync(ctx, ct, next);

        // next must still be called (pipeline must not abort)
        Assert.NotNull(getCapture());
        // no filter, no embedding override — original query preserved
        Assert.Null(getCapture()!.Options.Filter);
        Assert.Null(getCapture()!.Options.EmbeddingTextOverride);
        Assert.Contains(fakeLogger.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class FakeLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
