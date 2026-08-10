using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
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
    [InlineData("**Yes**")]
    [InlineData("\"yes\"")]
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
    [InlineData("**no**")]
    public async Task ClassifyAsync_ReadsAPlainNo(string reply)
    {
        // Markdown emphasis and stray quotes were classified Unparseable before, and every
        // wrongly-rejected verdict silently shrinks the denominator.
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

    [Theory]
    [InlineData("I'm sorry, I can't do that.")]
    [InlineData("""{"a":1}""")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("""["a"] trailing""")]
    [InlineData("Here you go: [\"a\"]")]
    public async Task ExtractListAsync_MalformedJson_ReportsFailureInsteadOfEmpty(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        // This is the defect that made a broken reply score 1.0: it was indistinguishable from
        // an answer that genuinely asserted nothing. Fence-stripping must not widen into general
        // salvage — a reply with JSON somewhere inside it is still not a reply we can score.
        Assert.False(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("""["a", null]""")]
    [InlineData("[null]")]
    public async Task ExtractListAsync_ArrayContainingNull_ReportsFailure(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        // Items is IReadOnlyList<string>, so a null element is one the consumer treats as
        // non-null: Faithfulness turned it into "Claim: ", the model answered the empty claim
        // arbitrarily, and that verdict landed in the denominator. A half-produced reply is not a
        // reply we can score, so the whole thing is rejected rather than quietly filtered.
        Assert.False(result.Parsed);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("```json\n[\"one\",\"two\"]\n```")]
    [InlineData("```\n[\"one\",\"two\"]\n```")]
    [InlineData("```JSON\n  [\"one\",\"two\"]  \n```")]
    public async Task ExtractListAsync_MarkdownFencedJson_ParsesItems(string reply)
    {
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        // Models fence JSON constantly outside structured-output mode. Reporting every such
        // sample unscoreable is honest but makes the metric report null against a fence-happy
        // model, which reads as a broken library.
        Assert.True(result.Parsed);
        Assert.Equal(new[] { "one", "two" }, result.Items);
    }

    [Theory]
    [InlineData("Here are the claims in JSON format:\n\n```\n[\"one\",\"two\"]\n```")]
    [InlineData("Sure! Here you go:\n\n```json\n[\"one\",\"two\"]\n```")]
    [InlineData("```\n[\"one\",\"two\"]\n```\n\nLet me know if you need anything else.")]
    public async Task ExtractListAsync_PreambleOrProseAroundAFence_ParsesItems(string reply)
    {
        // A fence states the model's intent as clearly as a fence-only reply does; the old strip
        // required the reply to START and END with one, so the commonest decoration — a sentence
        // before the fence — excluded the sample. This widens fence handling only: bare JSON
        // inside prose stays rejected (see MalformedJson_ReportsFailureInsteadOfEmpty).
        var judge = Judge(new RoutingChatClient([], fallback: reply));

        var result = await judge.ExtractListAsync("sys", "user", TestContext.Current.CancellationToken);

        Assert.True(result.Parsed);
        Assert.Equal(new[] { "one", "two" }, result.Items);
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
    public async Task ClassifyManyAsync_PreservesInputOrderWhenCallsCompleteInReverse()
    {
        // Rank-aware Context Precision depends on this: verdict k must belong to the chunk
        // retrieved at rank k. Completing the calls in reverse is what makes the test capable of
        // failing — with every call finishing synchronously in start order, an implementation
        // that collected results in completion order would pass identically.
        var client = new RoutingChatClient([("alpha", "no"), ("beta", "maybe")], fallback: "yes");
        client.GateCalls();
        var judge = Judge(client, new RagasOptions { MaxConcurrentCalls = 4 });

        var pending = judge.ClassifyManyAsync(
            "sys", ["alpha", "beta", "gamma"], item => item, TestContext.Current.CancellationToken);

        await WaitForAsync(() => client.CallCount >= 3, TestContext.Current.CancellationToken);
        await client.ReleaseInReverseAsync();
        var verdicts = await pending;

        // The three replies are deliberately all different: with only the middle one differing,
        // reversing the order would produce the same sequence and prove nothing.
        Assert.Equal(Verdict.No, verdicts[0]);
        Assert.Equal(Verdict.Unparseable, verdicts[1]);
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
    public async Task ClassifyAsync_SendsTheSystemAndUserPromptsAsGiven()
    {
        var client = new RoutingChatClient([], fallback: "yes");

        await Judge(client).ClassifyAsync("SYSTEM-TEXT", "USER-TEXT", TestContext.Current.CancellationToken);

        var prompt = Assert.Single(client.Prompts);
        Assert.Contains("SYSTEM-TEXT", prompt, StringComparison.Ordinal);
        Assert.Contains("USER-TEXT", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spins until <paramref name="condition"/> holds, or five seconds pass.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. The condition is satisfied on the first check today, but an unbounded
    /// spin turns any future regression that stops the judge starting its calls into a hung test
    /// run rather than a red one.
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
