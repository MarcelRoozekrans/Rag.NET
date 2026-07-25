using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
}
