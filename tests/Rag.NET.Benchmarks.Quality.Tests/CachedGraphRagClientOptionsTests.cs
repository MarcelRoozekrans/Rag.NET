using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality.GraphExtractions;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins that <see cref="CachedGraphRagClient"/> forwards a caller's <see cref="ChatOptions"/> to the
/// inner model, and keys the cache on them. <b>No LLM, no network.</b>
/// </summary>
/// <remarks>
/// Regression coverage for the 2026-08-29 runaway. <c>GetResponseAsync</c> accepted an
/// <c>options</c> parameter and never read it, and <c>CallOnceAsync</c> sent the client's own
/// baseline <see cref="ChatOptions"/> instead — so <c>FlareAnswerEngine</c>'s
/// <c>MaxOutputTokens = 150</c>, set specifically to bound rambling models, never reached the model.
/// A degenerate generation ran to 86,091 bytes (the same sentence 256 times) and timed out, twice,
/// because nothing capped it.
/// </remarks>
public sealed class CachedGraphRagClientOptionsTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        // Two short retries, for the reason HypotheticalCacheTests documents at length: an indexer
        // or a scanner briefly holding a just-written file must not fail a test that ran perfectly.
        foreach (var root in _roots)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }

                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                }

                Thread.Sleep(50);
            }
        }
    }

    /// <summary>The caller's options reach the model.</summary>
    /// <remarks>
    /// Regression test for the 2026-08-29 runaway. <c>FlareAnswerEngine</c> sets
    /// <c>MaxOutputTokens = 150</c> with a comment saying it exists to bound rambling models; this
    /// client discarded it, so a degenerate generation ran to 86,091 bytes and then timed out.
    /// </remarks>
    [Fact]
    public async Task CallerOptions_ReachTheInnerClient()
    {
        var inner = new OptionsRecordingChatClient(reply: "ok");
        var cache = new GraphExtractionCache(
            RootFor(nameof(CallerOptions_ReachTheInnerClient)),
            "openai/gpt-4o-mini@t0.0",
            GraphExtractionCacheMode.Fill);
        using var client = new CachedGraphRagClient(cache, inner, temperature: 0f);

        _ = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatOptions { MaxOutputTokens = 150 },
            Ct);

        Assert.Equal(150, inner.Received?.MaxOutputTokens);
        Assert.Equal(0f, inner.Received?.Temperature);
    }

    /// <summary>Constrained and unconstrained calls over one prompt are two cache entries.</summary>
    [Fact]
    public async Task ACallWithOptions_DoesNotHitTheEntryWrittenWithout()
    {
        var inner = new OptionsRecordingChatClient(reply: "unconstrained");
        var cache = new GraphExtractionCache(
            RootFor(nameof(ACallWithOptions_DoesNotHitTheEntryWrittenWithout)),
            "openai/gpt-4o-mini@t0.0",
            GraphExtractionCacheMode.Fill);
        using var client = new CachedGraphRagClient(cache, inner, temperature: 0f);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options: null, Ct);
        inner.Reply = "constrained";
        var second = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatOptions { MaxOutputTokens = 150 },
            Ct);

        Assert.Equal("constrained", second.Text);
    }

    /// <summary>
    /// A field that changes the response text but is not rendered into the key must throw rather
    /// than silently sharing a cache entry with a materially different request.
    /// </summary>
    /// <remarks>
    /// Regression coverage for the gap left after the 2026-08-29 fix: <c>Merge</c> forwards the
    /// caller's <em>whole</em> <see cref="ChatOptions"/> to the model, but
    /// <see cref="CachedGraphRagClient"/> only rendered <c>MaxOutputTokens</c>, <c>TopP</c> and
    /// <c>Seed</c> into the key. <see cref="ChatResponseFormat.Json"/> is the realistic offender —
    /// `DeepResearchRetriever.cs:96` already constructs one, one wiring change away from reaching
    /// this client — so it is the field exercised here rather than a synthetic one.
    /// </remarks>
    [Fact]
    public async Task AResponseAffectingOptionNotInTheKey_ThrowsRatherThanSharingAnEntry()
    {
        var inner = new OptionsRecordingChatClient(reply: "ok");
        var cache = new GraphExtractionCache(
            RootFor(nameof(AResponseAffectingOptionNotInTheKey_ThrowsRatherThanSharingAnEntry)),
            "openai/gpt-4o-mini@t0.0",
            GraphExtractionCacheMode.Fill);
        using var client = new CachedGraphRagClient(cache, inner, temperature: 0f);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            Ct));

        Assert.Contains("ResponseFormat", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The option shapes every current caller actually sends must not throw.</summary>
    /// <remarks>
    /// <c>ChatAnswerEngine</c>, <c>RefineAnswerEngine</c> and <c>MapReduceAnswerEngine</c> send an
    /// empty <see cref="ChatOptions"/>; <c>FlareAnswerEngine</c> sends <c>MaxOutputTokens</c> alone
    /// or with <c>Temperature</c>; the graph-extraction and community-report behaviors send
    /// <see langword="null"/>. None of these may start throwing because of the new guard.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ShapesInUseToday))]
    public async Task AShapeEveryCurrentCallerSends_DoesNotThrow(ChatOptions? options)
    {
        var inner = new OptionsRecordingChatClient(reply: "ok");
        var cache = new GraphExtractionCache(
            RootFor(nameof(AShapeEveryCurrentCallerSends_DoesNotThrow)),
            "openai/gpt-4o-mini@t0.0",
            GraphExtractionCacheMode.Fill);
        using var client = new CachedGraphRagClient(cache, inner, temperature: 0f);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options, Ct);

        Assert.Equal("ok", response.Text);
    }

    public static IEnumerable<object?[]> ShapesInUseToday()
    {
        yield return [null];
        yield return [new ChatOptions()];
        yield return [new ChatOptions { MaxOutputTokens = 150 }];
        yield return [new ChatOptions { MaxOutputTokens = 150, Temperature = 0.7f }];
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh, uniquely named cache root for one test, cleaned up on <see cref="Dispose"/>.</summary>
    private string RootFor(string testName)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ragnet-cached-graph-rag-client-" + testName + "-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }
}
