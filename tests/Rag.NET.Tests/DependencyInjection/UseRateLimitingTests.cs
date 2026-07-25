using System.Threading.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseRateLimitingTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IChatClient RespondingChatClient(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return client;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> RespondingEmbeddingGenerator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f })]));
        return generator;
    }

    private static Task<ChatResponse> AskAsync(IChatClient client, CancellationToken cancellationToken) =>
        client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cancellationToken);

    /// <summary>
    /// Manual-replenishment single-token bucket: with <c>AutoReplenishment = false</c> nothing
    /// ever refills it, so once its one token is consumed further acquisitions wait forever —
    /// a deterministic "exhausted" limiter (no timing involved).
    /// </summary>
    private static TokenBucketRateLimiter ExhaustibleBucket() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 1,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromTicks(1),
        QueueLimit = 10,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = false,
    });

    // ── Decoration ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UseRateLimiting_DecoratesPriorChatRegistration_CallFlowsThroughToOriginal()
    {
        var inner = RespondingChatClient("ok");
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(inner);
                rag.UseRateLimiting(o => o.ChatRequestsPerMinute = 600);
            })
            .BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        Assert.IsType<RateLimitedChatClient>(client);

        var result = await AskAsync(client, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Text);
        await inner.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UseRateLimiting_DecoratesPriorEmbeddingRegistration_CallFlowsThroughToOriginal()
    {
        var inner = RespondingEmbeddingGenerator();
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(inner);
                rag.UseRateLimiting(o => o.EmbeddingRequestsPerMinute = 600);
            })
            .BuildServiceProvider();

        var generator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        Assert.IsType<RateLimitedEmbeddingGenerator>(generator);

        var result = await generator.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Single(result);
        await inner.Received(1).GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UseRateLimiting_ChatOnly_LeavesEmbeddingGeneratorUndecorated()
    {
        var embedding = RespondingEmbeddingGenerator();
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(RespondingChatClient("ok"));
                rag.Services.AddSingleton(embedding);
                rag.UseRateLimiting(o => o.ChatRequestsPerMinute = 600);
            })
            .BuildServiceProvider();

        Assert.IsType<RateLimitedChatClient>(sp.GetRequiredService<IChatClient>());
        Assert.Same(embedding, sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void UseRateLimiting_NoPriorChatRegistration_ThrowsActionableAtRegistration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseRateLimiting(o => o.ChatRequestsPerMinute = 60)));

        Assert.Contains("IChatClient", ex.Message, StringComparison.Ordinal);
        Assert.Contains("before", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseRateLimiting_NoSurfaceConfigured_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseRateLimiting(_ => { })));

        Assert.Contains("at least one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseRateLimiting_NullConfigure_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseRateLimiting(null!)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void UseRateLimiting_NonPositiveChatRpm_Throws(int rpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseRateLimiting(o => o.ChatRequestsPerMinute = rpm)));
    }

    [Fact]
    public void UseRateLimiting_NonPositiveEmbeddingRpm_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseRateLimiting(o => o.EmbeddingRequestsPerMinute = 0)));
    }

    [Fact]
    public void UseRateLimiting_NonPositiveMaxQueuedRequests_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseRateLimiting(o =>
            {
                o.ChatRequestsPerMinute = 60;
                o.MaxQueuedRequests = 0;
            })));
    }

    // ── Limiter independence ─────────────────────────────────────────────────

    [Fact]
    public async Task UseRateLimiting_BothSurfaces_GetIndependentLimiters()
    {
        var chatInner = RespondingChatClient("ok");
        var embeddingInner = RespondingEmbeddingGenerator();
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(chatInner);
                rag.Services.AddSingleton(embeddingInner);
                rag.UseRateLimiting(o =>
                {
                    o.ChatRequestsPerMinute = 60;
                    o.EmbeddingRequestsPerMinute = 60;
                });
                // Deterministic seam: swap the chat-surface limiter for one whose bucket
                // never refills (keyed last-wins), so "exhausted" needs no timing tricks.
                rag.Services.AddKeyedSingleton<IRateLimiter>(
                    RagBuilderExtensions.ChatRateLimiterKey,
                    (_, _) => new TokenBucketRateLimiterAdapter(ExhaustibleBucket(), "chat"));
            })
            .BuildServiceProvider();

        Assert.NotSame(
            sp.GetRequiredKeyedService<IRateLimiter>(RagBuilderExtensions.ChatRateLimiterKey),
            sp.GetRequiredKeyedService<IRateLimiter>(RagBuilderExtensions.EmbeddingRateLimiterKey));

        var chat = sp.GetRequiredService<IChatClient>();
        var embeddings = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        // Exhaust the chat surface: the single token is consumed, the next call waits forever.
        await AskAsync(chat, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var blockedChatCall = AskAsync(chat, cts.Token);
        Assert.False(blockedChatCall.IsCompleted);

        // The embedding surface is unaffected by the exhausted chat limiter.
        await embeddings.GenerateAsync(["a"], cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await embeddings.GenerateAsync(["b"], cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(blockedChatCall.IsCompleted);
        await chatInner.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        // Clean up the deterministically blocked call.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            blockedChatCall.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
