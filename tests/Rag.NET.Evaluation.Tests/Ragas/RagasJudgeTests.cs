using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class RagasJudgeTests
{
    private static RagasJudge Judge(IChatClient client, RagasOptions? options = null, ICostLedger? ledger = null)
        => new(client, options ?? new RagasOptions(), ledger);

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes.")]
    [InlineData("YES")]
    public async Task ClassifyAsync_ReadsAPlainYes(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var verdict = await judge.ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.Yes, verdict);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("No.")]
    [InlineData("NO")]
    public async Task ClassifyAsync_ReadsAPlainNo(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var verdict = await judge.ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.No, verdict);
    }

    [Theory]
    [InlineData("Yes, but only partially.")]
    [InlineData("The claim is supported by the context.")]
    [InlineData("")]
    [InlineData("maybe")]
    public async Task ClassifyAsync_AmbiguousReply_IsUnparseableNotAGuess(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var verdict = await judge.ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        // "Yes, but only partially" counted as full support before 3.1, and "The claim is
        // supported" counted as unsupported. Both were StartsWith("yes") artefacts.
        Assert.Equal(Verdict.Unparseable, verdict);
    }

    [Fact]
    public async Task ExtractListAsync_ValidJson_ParsesItems()
    {
        var judge = Judge(new RoutingChatClient([], fallback: """["one","two"]"""));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.True(result.Parsed);
        Assert.Equal(new[] { "one", "two" }, result.Items);
    }

    [Fact]
    public async Task ExtractListAsync_EmptyArray_ParsesAsGenuinelyEmpty()
    {
        var judge = Judge(new RoutingChatClient([], fallback: "[]"));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.True(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ExtractListAsync_MalformedJson_ReportsFailureInsteadOfEmpty()
    {
        var judge = Judge(new RoutingChatClient([], fallback: "I'm sorry, I can't do that."));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        // This is the defect that made a broken reply score 1.0: it was indistinguishable from
        // an answer that genuinely asserted nothing.
        Assert.False(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ExtractListAsync_EmptyReply_ReportsFailure()
    {
        var judge = Judge(new RoutingChatClient([], fallback: "   "));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.False(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ClassifyManyAsync_RespectsTheConcurrencyCeiling()
    {
        var client = new RoutingChatClient([], fallback: "yes");
        client.GateCalls();
        var judge = Judge(client, new RagasOptions { MaxConcurrentCalls = 2 });
        var items = new List<string>();
        for (var i = 0; i < 10; i++)
            items.Add($"item {i}");

        var pending = judge.ClassifyManyAsync("sys", items, _ => "u", TestContext.Current.CancellationToken);

        // Wait until the judge has started as many as it is going to, then release.
        await WaitForAsync(() => client.CallCount >= 2, TestContext.Current.CancellationToken);
        client.ReleaseAll();
        await pending;

        Assert.Equal(10, client.CallCount);
        Assert.True(client.PeakInFlight <= 2, $"peak was {client.PeakInFlight}, ceiling was 2");
    }

    [Fact]
    public async Task ClassifyManyAsync_WithoutACeiling_StillCompletesEveryItem()
    {
        var client = new RoutingChatClient([], fallback: "yes");
        var judge = Judge(client, new RagasOptions { MaxConcurrentCalls = 100 });
        var items = new List<string>();
        for (var i = 0; i < 5; i++)
            items.Add($"item {i}");

        var verdicts = await judge.ClassifyManyAsync("sys", items, _ => "u", TestContext.Current.CancellationToken);

        Assert.Equal(5, verdicts.Count);
        Assert.All(verdicts, v => Assert.Equal(Verdict.Yes, v));
    }

    [Fact]
    public async Task ClassifyManyAsync_PreservesInputOrder()
    {
        // Rank-aware Context Precision depends on this: verdict i must belong to item i.
        var client = new RoutingChatClient([("beta", "no")], fallback: "yes");
        var judge = Judge(client, new RagasOptions { MaxConcurrentCalls = 4 });

        var verdicts = await judge.ClassifyManyAsync(
            "sys", ["alpha", "beta", "gamma"], item => item, TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.Yes, verdicts[0]);
        Assert.Equal(Verdict.No, verdicts[1]);
        Assert.Equal(Verdict.Yes, verdicts[2]);
    }

    [Fact]
    public void Constructor_ZeroConcurrency_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Judge(new RoutingChatClient([]), new RagasOptions { MaxConcurrentCalls = 0 }));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task ClassifyAsync_WithUsageAndPrices_RecordsCost()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 },
        };
        var options = new RagasOptions { PricePerInputToken = 0.001m, PricePerOutputToken = 0.002m };

        await Judge(client, options, ledger).ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(CostKind.Chat, entry.Kind);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(10, entry.OutputTokens);
        Assert.Equal((100 * 0.001m) + (10 * 0.002m), entry.Cost);
    }

    [Fact]
    public async Task ClassifyAsync_WhenTheModelReportsNoUsage_RecordsNothing()
    {
        var ledger = new RecordingCostLedger();
        var client = new RoutingChatClient([], fallback: "yes") { Usage = null };

        await Judge(client, new RagasOptions { PricePerInputToken = 1m }, ledger)
            .ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        // Recording a zero-token entry would state as fact that the call was free.
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task ClassifyAsync_WithoutALedger_StillJudges()
    {
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
        };

        var verdict = await Judge(client).ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.Yes, verdict);
    }

    [Fact]
    public async Task ClassifyAsync_WhenTheLedgerThrows_DoesNotFailTheEvaluation()
    {
        var client = new RoutingChatClient([], fallback: "yes")
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 },
        };

        var verdict = await Judge(client, new RagasOptions(), new ThrowingCostLedger())
            .ClassifyAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.Equal(Verdict.Yes, verdict);
    }

    [Fact]
    public async Task ClassifyAsync_SendsTheSystemAndUserPromptsAsGiven()
    {
        var client = new RoutingChatClient([], fallback: "yes");

        await Judge(client).ClassifyAsync("SYSTEM-TEXT", "USER-TEXT", TestContext.Current.CancellationToken);

        var prompt = Assert.Single(client.Prompts);
        Assert.Contains("SYSTEM-TEXT", prompt, StringComparison.Ordinal);
        Assert.Contains("USER-TEXT", prompt, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
