using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Internal;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests;

/// <summary>
/// Cost recording moved here with the code it tests: the ledger entry is written by the shared
/// caller, not by the RAGAS judge, and the dataset builder will be billed through the same path.
/// The judge keeps the tests for what is still its own — verdicts, JSON extraction, fences.
/// </summary>
public sealed class EvaluationChatCallerTests
{
    private static EvaluationChatCaller Caller(
        IChatClient client, EvaluationCallOptions? options = null, ICostLedger? ledger = null)
        => new(client, options ?? new CallOptions(), ledger);

    [Fact]
    public async Task CompleteAsync_WithUsageAndPrices_RecordsCost()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 },
        };
        var options = new CallOptions { PricePerInputToken = 0.001m, PricePerOutputToken = 0.002m };

        await Caller(client, options, ledger).CompleteAsync("sys", "user", TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(CostKind.Chat, entry.Kind);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(10, entry.OutputTokens);
        Assert.Equal((100 * 0.001m) + (10 * 0.002m), entry.Cost);
    }

    [Fact]
    public async Task CompleteAsync_WhenTheModelReportsNoUsage_RecordsNothing()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes") { Usage = null };

        await Caller(client, new CallOptions { PricePerInputToken = 1m }, ledger)
            .CompleteAsync("sys", "user", TestContext.Current.CancellationToken);

        // Recording a zero-token entry would state as fact that the call was free.
        Assert.Empty(ledger.Entries);
    }

    public static TheoryData<UsageDetails> PartiallyReportedUsage => new()
    {
        new UsageDetails(),                                 // present, but nothing filled in
        new UsageDetails { InputTokenCount = 100 },         // input only
        new UsageDetails { OutputTokenCount = 10 },         // output only
        new UsageDetails { TotalTokenCount = 110 },         // a total, but neither side
    };

    [Theory]
    [MemberData(nameof(PartiallyReportedUsage))]
    public async Task CompleteAsync_WhenUsageIsPartiallyReported_RecordsNothing(UsageDetails usage)
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes") { Usage = usage };

        await Caller(client, new CallOptions { PricePerInputToken = 1m, PricePerOutputToken = 1m }, ledger)
            .CompleteAsync("sys", "user", TestContext.Current.CancellationToken);

        // A present-but-empty UsageDetails is not a report of zero. Filling the missing side with
        // 0 would understate spend, which is worse than recording nothing: the entry looks
        // authoritative. Both counters must come from the provider or neither is trusted.
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task CompleteAsync_WithoutALedger_StillReturnsTheReply()
    {
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
        };

        var reply = await Caller(client).CompleteAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal("yes", reply);
    }

    [Fact]
    public async Task CompleteAsync_WhenTheLedgerThrows_DoesNotFailTheCall()
    {
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
        };

        var reply = await Caller(client, new CallOptions(), new ThrowingCostLedger())
            .CompleteAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal("yes", reply);
    }

    /// <summary>
    /// A minimal concrete <see cref="EvaluationCallOptions"/>, so these tests pin the caller rather
    /// than any one feature's options type.
    /// </summary>
    private sealed class CallOptions : EvaluationCallOptions;
}
