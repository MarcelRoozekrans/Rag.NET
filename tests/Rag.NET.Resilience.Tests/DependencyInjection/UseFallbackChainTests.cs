using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseFallbackChainTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IChatClient RespondingClient(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return client;
    }

    private static IChatClient RateLimitedClient()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("429", null, System.Net.HttpStatusCode.TooManyRequests));
        return client;
    }

    // Chat call that never completes but honors cancellation (like a real HTTP client)
    private static async Task<ChatResponse> HangUntilCancelledAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void UseFallbackChain_RegistersFallbackChatClientAsIChatClient()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFallbackChain(o => o
                .AddClient(_ => RespondingClient("a"))
                .AddClient(_ => RespondingClient("b"))))
            .BuildServiceProvider();

        Assert.IsType<FallbackChatClient>(sp.GetRequiredService<IChatClient>());
    }

    [Fact]
    public async Task UseFallbackChain_OrderHonored_FirstClientTransientFailure_SecondServes()
    {
        var primary = RateLimitedClient();
        var secondary = RespondingClient("fallback ok");

        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFallbackChain(o => o
                .AddClient(_ => primary)
                .AddClient(_ => secondary)))
            .BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        var result = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fallback ok", result.Text);
        await primary.Received(1).GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UseFallbackChain_FewerThanTwoClients_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddRagNet(rag => rag.UseFallbackChain(o => o
                .AddClient(_ => RespondingClient("only")))));

        Assert.Contains("at least 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseFallbackChain_NullConfigure_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddRagNet(rag => rag.UseFallbackChain(null!)));
    }

    [Fact]
    public void UseFallbackChain_NonPositivePerClientTimeout_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddRagNet(rag => rag.UseFallbackChain(o =>
            {
                o.AddClient(_ => RespondingClient("a"));
                o.AddClient(_ => RespondingClient("b"));
                o.PerClientTimeout = TimeSpan.Zero;
            })));
    }

    [Fact]
    public async Task UseFallbackChain_PerClientTimeoutFlowsThroughDI_HungPrimaryFallsBack()
    {
        var primary = Substitute.For<IChatClient>();
        primary.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => HangUntilCancelledAsync(ci.Arg<CancellationToken>()));
        var secondary = RespondingClient("fallback ok");

        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFallbackChain(o =>
            {
                o.AddClient(_ => primary);
                o.AddClient(_ => secondary);
                o.PerClientTimeout = TimeSpan.FromMilliseconds(50);
            }))
            .BuildServiceProvider();

        var client = sp.GetRequiredService<IChatClient>();
        var result = await client
            .GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("fallback ok", result.Text);
    }

    [Fact]
    public void UseFallbackChain_NullFactoryAddedDirectlyToClients_ThrowsActionableAtResolve()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag => rag.UseFallbackChain(o =>
            {
                o.AddClient(_ => RespondingClient("a"));
                o.Clients.Add(null!); // bypasses AddClient's null guard
            }))
            .BuildServiceProvider();

        var ex = Assert.Throws<ArgumentException>(() => sp.GetRequiredService<IChatClient>());
        Assert.Contains("null factory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseFallbackChain_SupersedesPriorIChatClientRegistration()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                // Simulates a prior provider registration (e.g. AddSingleton<IChatClient>(openAiClient)).
                rag.Services.AddSingleton(RespondingClient("direct"));
                rag.UseFallbackChain(o => o
                    .AddClient(_ => RespondingClient("a"))
                    .AddClient(_ => RespondingClient("b")));
            })
            .BuildServiceProvider();

        Assert.IsType<FallbackChatClient>(sp.GetRequiredService<IChatClient>());
    }

    /// <summary>
    /// Issue #195: superseding a prior registration is the documented direction, and the reverse is
    /// not. A provider client registered <b>after</b> the chain replaced it wholesale, leaving a
    /// fallback chain that was configured, validated ("at least 2 clients") and unreachable —
    /// discovered during the outage it was configured for.
    /// </summary>
    [Fact]
    public void UseFallbackChain_SupersededByALaterChatClient_FailsLoudly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVectorStore>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddRagNet(rag => rag.UseFallbackChain(o => o
            .AddClient(_ => RespondingClient("a"))
            .AddClient(_ => RespondingClient("b"))));

        services.AddSingleton(RespondingClient("the provider client"));

        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IRagPipeline>());

        Assert.Contains("UseFallbackChain", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IChatClient", ex.Message, StringComparison.Ordinal);
    }
}
