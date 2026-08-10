using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

public class CostTrackingChatClientTests
{
    private static readonly Tokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    private static CostBudgetOptions DailyOptions(decimal limit, decimal inputPrice = 0m, decimal outputPrice = 0m) => new()
    {
        DailyLimit = limit,
        InputPricePerMTokens = inputPrice,
        OutputPricePerMTokens = outputPrice,
    };

    private static IList<ChatMessage> Messages(string text) => [new ChatMessage(ChatRole.User, text)];

    private static IChatClient RespondingInner(ChatResponse response)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(response);
        return inner;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldUpdates(params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    // ── Pre-call gate ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(9.99, false)] // under: proceeds
    [InlineData(10.00, true)] // at: blocked
    [InlineData(10.01, true)] // over: blocked
    public async Task GetResponseAsync_DailyLimitGate(decimal spend, bool blocked)
    {
        var ledger = new FakeCostLedger { DaySpend = spend };
        var inner = RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var sut = new CostTrackingChatClient(inner, ledger, DailyOptions(10m));

        if (blocked)
        {
            var ex = await Assert.ThrowsAsync<BudgetExceededException>(() =>
                sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(CostWindow.Day, ex.Window);
            Assert.Equal(10m, ex.Limit);
            Assert.Equal(spend, ex.Spend);
            await inner.DidNotReceive().GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        }
        else
        {
            var response = await sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("ok", response.Text);
        }
    }

    [Fact]
    public async Task GetResponseAsync_MonthlyLimitReached_ThrowsWithMonthWindow()
    {
        var ledger = new FakeCostLedger { DaySpend = 0.5m, MonthSpend = 100m };
        var sut = new CostTrackingChatClient(
            RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            ledger,
            new CostBudgetOptions { DailyLimit = 10m, MonthlyLimit = 100m });

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CostWindow.Month, ex.Window);
        Assert.Equal(100m, ex.Limit);
        Assert.Equal(100m, ex.Spend);
    }

    [Fact]
    public async Task BudgetExceededException_MessageCarriesWindowLimitAndSpend()
    {
        var ledger = new FakeCostLedger { DaySpend = 12.5m };
        var sut = new CostTrackingChatClient(
            RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            ledger,
            DailyOptions(10m));

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Day", ex.Message, StringComparison.Ordinal);
        Assert.Contains("10", ex.Message, StringComparison.Ordinal);
        Assert.Contains("12.5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_OnlyMonthlyLimitConfigured_DoesNotReadDayWindow()
    {
        var log = new List<string>();
        var ledger = new FakeCostLedger(log);
        var sut = new CostTrackingChatClient(
            RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            ledger,
            new CostBudgetOptions { MonthlyLimit = 100m });

        await sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ledger-read:Month", "ledger-record" }, log, StringComparer.Ordinal);
    }

    // ── Recording: provider usage vs estimation ──────────────────────────────

    [Fact]
    public async Task GetResponseAsync_ProviderReportsFullUsage_RecordsReportedCounts()
    {
        var ledger = new FakeCostLedger();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 40 },
        };
        var sut = new CostTrackingChatClient(
            RespondingInner(response), ledger, DailyOptions(10m, inputPrice: 3m, outputPrice: 15m));

        await sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Recorded);
        Assert.Equal(CostKind.Chat, entry.Kind);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(40, entry.OutputTokens);
        // 100/1M * 3 + 40/1M * 15 = 0.0003 + 0.0006
        Assert.Equal(0.0009m, entry.Cost);
    }

    [Fact]
    public async Task GetResponseAsync_NoUsage_RecordsTiktokenEstimates()
    {
        const string Prompt = "How many retrieval techniques does Rag.NET ship?";
        const string Answer = "Quite a few — hybrid, HyDE, FLARE, and more.";
        var ledger = new FakeCostLedger();
        var sut = new CostTrackingChatClient(
            RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer))),
            ledger,
            DailyOptions(10m));

        await sut.GetResponseAsync(Messages(Prompt), cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Recorded);
        Assert.Equal(s_tokenizer.CountTokens(Prompt), entry.InputTokens);
        Assert.Equal(s_tokenizer.CountTokens(Answer), entry.OutputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_PartialUsage_FallsBackToEstimationForBothSides()
    {
        const string Prompt = "hello there";
        const string Answer = "general kenobi";
        var ledger = new FakeCostLedger();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer))
        {
            Usage = new UsageDetails { InputTokenCount = 12345 }, // output missing → untrusted
        };
        var sut = new CostTrackingChatClient(RespondingInner(response), ledger, DailyOptions(10m));

        await sut.GetResponseAsync(Messages(Prompt), cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Recorded);
        Assert.Equal(s_tokenizer.CountTokens(Prompt), entry.InputTokens);
        Assert.Equal(s_tokenizer.CountTokens(Answer), entry.OutputTokens);
    }

    // ── Streaming ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStreamingResponseAsync_RecordsOnceAfterCompletion_UsingEmittedUsage()
    {
        var ledger = new FakeCostLedger();
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(YieldUpdates(
                new ChatResponseUpdate { Contents = [new TextContent("Hello ")] },
                new ChatResponseUpdate { Contents = [new TextContent("world")] },
                new ChatResponseUpdate
                {
                    Contents = [new UsageContent(new UsageDetails { InputTokenCount = 7, OutputTokenCount = 2 })],
                }));
        var sut = new CostTrackingChatClient(inner, ledger, DailyOptions(10m, inputPrice: 1m, outputPrice: 2m));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in sut.GetStreamingResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
            Assert.Empty(ledger.Recorded); // nothing recorded mid-stream
        }

        Assert.Equal(3, updates.Count);
        var entry = Assert.Single(ledger.Recorded);
        Assert.Equal(7, entry.InputTokens);
        Assert.Equal(2, entry.OutputTokens);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_NoUsageEmitted_EstimatesFromAccumulatedText()
    {
        const string Prompt = "what is up";
        var ledger = new FakeCostLedger();
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(YieldUpdates(
                new ChatResponseUpdate { Contents = [new TextContent("The sky, ")] },
                new ChatResponseUpdate { Contents = [new TextContent("mostly.")] }));
        var sut = new CostTrackingChatClient(inner, ledger, DailyOptions(10m));

        await foreach (var _ in sut.GetStreamingResponseAsync(Messages(Prompt), cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        var entry = Assert.Single(ledger.Recorded);
        Assert.Equal(s_tokenizer.CountTokens(Prompt), entry.InputTokens);
        Assert.Equal(s_tokenizer.CountTokens("The sky, mostly."), entry.OutputTokens);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_CancelledMidStream_RecordsNothing()
    {
        var ledger = new FakeCostLedger();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => CancellingStream(ci.ArgAt<CancellationToken>(2)));
        var sut = new CostTrackingChatClient(inner, ledger, DailyOptions(10m));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(Messages("hi"), cancellationToken: cts.Token))
            {
                await cts.CancelAsync(); // cancel after the first update
            }
        });

        Assert.Empty(ledger.Recorded); // a partial stream's usage is unknown: never guessed

        async IAsyncEnumerable<ChatResponseUpdate> CancellingStream(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate { Contents = [new TextContent("partial")] };
            token.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate { Contents = [new TextContent("never reached")] };
        }
    }

    [Fact]
    public async Task GetStreamingResponseAsync_BudgetExhausted_ThrowsBeforeInnerStreamStarts()
    {
        var ledger = new FakeCostLedger { DaySpend = 10m };
        var inner = Substitute.For<IChatClient>();
        var sut = new CostTrackingChatClient(inner, ledger, DailyOptions(10m));

        await Assert.ThrowsAsync<BudgetExceededException>(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        inner.DidNotReceive().GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    // ── Ledger degradation ───────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_LedgerReadFails_CallProceedsUngated()
    {
        var ledger = new FakeCostLedger { ThrowOnRead = new IOException("disk full") };
        var sut = new CostTrackingChatClient(
            RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            ledger,
            DailyOptions(0m)); // limit already blown — but the ledger is unreadable

        var response = await sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_LedgerRecordFails_CallStillSucceeds()
    {
        var ledger = new FakeCostLedger { ThrowOnRecord = new IOException("disk full") };
        var sut = new CostTrackingChatClient(
            RespondingInner(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            ledger,
            DailyOptions(10m));

        var response = await sut.GetResponseAsync(Messages("hi"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        Assert.Empty(ledger.Recorded);
    }

    // ── Delegation and ownership ─────────────────────────────────────────────

    [Fact]
    public void GetService_AnswersForItsOwnType_ThenDelegates()
    {
        var inner = Substitute.For<IChatClient>();
        var sentinel = new object();
        inner.GetService(typeof(string), "key").Returns(sentinel);
        var sut = new CostTrackingChatClient(inner, new FakeCostLedger(), DailyOptions(1m));

        Assert.Same(sut, sut.GetService(typeof(CostTrackingChatClient)));
        Assert.Same(sut, sut.GetService(typeof(IChatClient)));
        Assert.Same(sentinel, sut.GetService(typeof(string), "key"));
    }

    [Fact]
    public void Dispose_DoesNotDisposeInner()
    {
        var inner = Substitute.For<IChatClient>();

        new CostTrackingChatClient(inner, new FakeCostLedger(), DailyOptions(1m)).Dispose();

        inner.DidNotReceive().Dispose();
    }
}
