using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Resilience;
using Rag.NET.Tests.Resilience;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

/// <summary>
/// Proves the documented resilience composition: registering
/// <c>UseFallbackChain → UseRateLimiting → UseCostBudgeting</c> (inner to outer) yields
/// <c>CostTracking(RateLimited(Fallback(provider)))</c> — the budget gate runs first, then
/// the rate limiter, then the provider — and each layer is probeable via GetService.
/// </summary>
public class ResilienceStackingTests
{
    private sealed class RecordingChatClient(List<string> log, string name) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class LoggingRateLimiter(List<string> log) : IRateLimiter
    {
        public ValueTask AcquireAsync(int permits = 1, CancellationToken cancellationToken = default)
        {
            log.Add("limiter-acquire");
            return ValueTask.CompletedTask;
        }

        public void Dispose() { }
    }

    private static ServiceProvider BuildDocumentedStack(List<string> log, FakeCostLedger ledger) =>
        new ServiceCollection()
            .AddRagNet(rag =>
            {
                // Custom ledger BEFORE UseCostBudgeting so TryAdd keeps it (documented).
                rag.Services.AddSingleton<ICostLedger>(ledger);

                // The documented order, inner to outer:
                rag.UseFallbackChain(o =>
                {
                    o.AddClient(_ => new RecordingChatClient(log, "provider-primary"));
                    o.AddClient(_ => new RecordingChatClient(log, "provider-secondary"));
                });
                rag.UseRateLimiting(o => o.ChatRequestsPerMinute = 600);
                rag.UseCostBudgeting(o => o.DailyLimit = 10m);

                // Deterministic seam: swap the chat limiter for a logging one (keyed last-wins)
                // so acquisition shows up in the shared call-order log.
                rag.Services.AddKeyedSingleton<IRateLimiter>(
                    ResilienceBuilderExtensions.ChatRateLimiterKey, (_, _) => new LoggingRateLimiter(log));
            })
            .BuildServiceProvider();

    [Fact]
    public async Task DocumentedOrder_BudgetGateThenLimiterThenProvider()
    {
        var log = new List<string>();
        var ledger = new FakeCostLedger(log);
        using var sp = BuildDocumentedStack(log, ledger);

        var client = sp.GetRequiredService<IChatClient>();
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        // Cheapest gate first: ledger read (budget gate) → permit acquire → provider call,
        // then the post-call usage record. A blown budget would consume no permit; a
        // throttled call would start no fallback sequence.
        Assert.Equal(new[] { "ledger-read:Day", "limiter-acquire", "provider-primary", "ledger-record" }, log, StringComparer.Ordinal);
    }

    [Fact]
    public void DocumentedOrder_EveryLayerProbeableViaGetService()
    {
        var log = new List<string>();
        using var sp = BuildDocumentedStack(log, new FakeCostLedger(log));

        var client = sp.GetRequiredService<IChatClient>();

        var outer = Assert.IsType<CostTrackingChatClient>(client);
        Assert.Same(outer, outer.GetService(typeof(CostTrackingChatClient)));
        Assert.IsType<RateLimitedChatClient>(outer.GetService(typeof(RateLimitedChatClient)));
        Assert.IsType<FallbackChatClient>(outer.GetService(typeof(FallbackChatClient)));
    }

    [Fact]
    public async Task DocumentedOrder_BlownBudget_ConsumesNoRatePermitAndCallsNoProvider()
    {
        var log = new List<string>();
        var ledger = new FakeCostLedger(log) { DaySpend = 10m };
        using var sp = BuildDocumentedStack(log, ledger);

        var client = sp.GetRequiredService<IChatClient>();
        await Assert.ThrowsAsync<Rag.NET.Models.BudgetExceededException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(new[] { "ledger-read:Day" }, log, StringComparer.Ordinal); // no permit consumed, no provider touched
    }
}
