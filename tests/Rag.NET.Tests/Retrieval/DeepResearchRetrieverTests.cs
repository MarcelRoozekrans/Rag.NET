using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.Retrieval;

public class DeepResearchRetrieverTests
{
    private static SearchResult MakeResult(string docId, int chunkIndex, double score = 1.0) =>
        new()
        {
            Chunk = new TextChunk { Text = "text", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex },
            Score = score,
        };

    private static Result<IReadOnlyList<SearchResult>, RagError> Ok(params SearchResult[] results) =>
        Result<IReadOnlyList<SearchResult>, RagError>.Success(results);

    private static void ReturnSufficient(IChatClient chatClient) =>
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":true,\"subQueries\":[]}")));

    private static void ReturnInsufficientThenSufficient(IChatClient chatClient, params string[] subQueries)
    {
        var list = string.Join(",", subQueries.Select(q => $"\"{q}\""));
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, $"{{\"sufficient\":false,\"subQueries\":[{list}]}}")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":true,\"subQueries\":[]}")));
    }

    [Fact]
    public async Task SufficientOnFirstPass_ReturnsChunks_NoSubQueries()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        ReturnSufficient(chatClient);

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        _ = await inner.Received(1).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct);
    }

    [Fact]
    public async Task InsufficientThenSufficient_MergesSubQueryResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q",    Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc2", 0)));
        ReturnInsufficientThenSufficient(chatClient, "sub1");

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
    }

    [Fact]
    public async Task MaxDepthReached_StopsLoop_ReturnsAccumulatedChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("doc1", 0)));
        // Always insufficient
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":false,\"subQueries\":[\"sub1\"]}")));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions { MaxDepth = 2 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // Exactly MaxDepth sufficiency checks — loop stopped
        await chatClient.Received(2)
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A chunk both queries found appears once, and finding it twice makes it rank higher rather
    /// than merely keeping the larger of two scores.
    /// </summary>
    /// <remarks>
    /// This replaced <c>DuplicateChunks_Deduplicated_HighestScoreKept</c>, which asserted that the
    /// survivor kept <c>0.9</c>. Deduplication is unchanged — the assertion that changed is what
    /// the survivor's score means. "Keep the highest" picked the larger of two numbers produced by
    /// two different query vectors, which is the comparison #475 says cannot be made. Under rank
    /// fusion the two sightings <i>accumulate</i>, so agreement between the question and its
    /// sub-query is what promotes the chunk. Rewritten rather than widened, deliberately.
    /// </remarks>
    [Fact]
    public async Task ChunkFoundByBothQueries_AppearsOnce_AndOutranksChunksFoundByOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();

        // doc1 is found by both. doc3 has the highest raw score of anything here and is found only
        // by the sub-query, so under the old "sort by score" it would have led the page.
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("doc1", 0, 0.9), MakeResult("doc2", 0, 0.5)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("doc1", 0, 0.7), MakeResult("doc3", 0, 0.99)));
        ReturnInsufficientThenSufficient(chatClient, "sub1");

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", new RetrievalOptions { TopK = 10 }, ct);

        Assert.True(result.IsSuccess);

        // Deduplicated: three distinct chunks from four hits.
        Assert.Equal(3, result.Value.Count);
        Assert.Single(result.Value, r => string.Equals(r.Chunk.DocumentId.Value, "doc1", StringComparison.Ordinal));

        // And the one both queries ranked first leads, ahead of the higher raw score.
        Assert.Equal("doc1", result.Value[0].Chunk.DocumentId.Value);
        Assert.True(
            result.Value[0].Score > result.Value[1].Score,
            "a chunk found by both rankings must score above one found by a single ranking");
    }

    [Fact]
    public async Task SubQueryRetrievalThrows_LoggedAndSkipped_OtherResultsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q",    Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct).ThrowsAsync(new HttpRequestException("down"));
        ReturnInsufficientThenSufficient(chatClient, "sub1");

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value); // sub1 threw, doc1 still present
    }

    [Fact]
    public async Task InnerRetrieverFails_PropagatesFailureWithoutCallingLlm()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        var failure = Result<IReadOnlyList<SearchResult>, RagError>.Failure(
            new RagError.StorageFailed(new InvalidOperationException("storage error")));
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(failure);

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.False(result.IsSuccess);
        await chatClient.DidNotReceive()
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    private const string InsufficientPayload = """{"sufficient":false,"subQueries":["sub1"]}""";

    public static TheoryData<string, string> WrappedResponseShapes() => new()
    {
        { "preamble then unlabelled fence", $"Here is my assessment in JSON format:\n\n```\n{InsufficientPayload}\n```" },
        { "preamble then labelled fence", $"Sure! Here you go:\n\n```json\n{InsufficientPayload}\n```" },
        { "preamble then bare json", $"Here is the JSON:\n\n{InsufficientPayload}" },
        { "labelled fence only", $"```json\n{InsufficientPayload}\n```" },
        { "fence then trailing prose", $"```\n{InsufficientPayload}\n```\n\nLet me know if you need anything else." },
    };

    [Theory]
    [MemberData(nameof(WrappedResponseShapes))]
    public async Task WhenLlmWrapsTheVerdict_DeepeningStillHappens(string shape, string response)
    {
        // ResponseFormat = Json is a request, not a guarantee. Against a provider that ignores
        // it, every wrapped verdict used to throw, be caught as "sufficient", and silently turn
        // the whole feature into a passthrough of the inner retriever.
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q",    Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        inner.RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc2", 0)));
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, response)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"sufficient\":true,\"subQueries\":[]}")));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.True(
            result.Value.Count == 2,
            $"The '{shape}' verdict did not trigger a sub-query. The JSON inside it is valid and " +
            "says insufficient, so parsing failed to find it and deep research silently became a " +
            "passthrough.");
        _ = await inner.Received(1).RetrieveAsync("sub1", Arg.Any<RetrievalOptions?>(), ct);
    }

    [Fact]
    public async Task MalformedLlmJson_TreatedAsSufficient_Passthrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json {{{")));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        _ = await inner.Received(1).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct);
    }

    [Fact]
    public async Task LlmTransportFails_TreatedAsSufficient_Passthrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct).Returns(Ok(MakeResult("doc1", 0)));
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        _ = await inner.Received(1).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct);
    }

    [Fact]
    public async Task SubQueryCountCap_Respected_OnlySubQueryCountQueriesIssued()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();
        inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct)
             .Returns(Ok(MakeResult("doc1", 0)));
        // LLM returns 4 sub-queries but SubQueryCount = 2 → only 2 should be retrieved
        chatClient
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    "{\"sufficient\":false,\"subQueries\":[\"s1\",\"s2\",\"s3\",\"s4\"]}")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    "{\"sufficient\":true,\"subQueries\":[]}")));

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions { SubQueryCount = 2 });
        var result = await sut.RetrieveAsync("q", null, ct);

        Assert.True(result.IsSuccess);
        // Original query + 2 capped sub-queries = 3 total retrieve calls
        _ = await inner.Received(3).RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct);
    }

    /// <summary>
    /// Pins that the returned page can exceed <see cref="RetrievalOptions.TopK"/>, which the
    /// property documents as "chunks to return after all pipeline stages".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a characterisation test, not an endorsement.</b> Every sub-query's page is
    /// appended and the union deduplicated; nothing truncates. At the shipped defaults —
    /// <see cref="DeepResearchOptions.MaxDepth"/> 3 and
    /// <see cref="DeepResearchOptions.SubQueryCount"/> 3 — a caller asking for 10 can receive up to
    /// 100, and <c>RagPipeline.RetrieveAsync</c> returns the retriever's list unchanged.
    /// </para>
    /// <para>
    /// <b>The other half of the surprise is the ordering.</b> The union is sorted by score, but a
    /// sub-query's scores come from a different query vector than the caller's, so they are not
    /// comparable — a chunk scoring 0.9 against a sub-query outranks one scoring 0.8 against the
    /// question actually asked. Any nDCG read off this page inherits that.
    /// </para>
    /// <para>
    /// <b>Both halves are now fixed (#475), and this test was inverted rather than widened</b> — it
    /// previously asserted <c>topK + 3 * topK</c> and carried a message saying it should fail when
    /// the truncation landed. It did, and this is that rewrite.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReturnedPage_IsTruncatedToTopK()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();

        const int topK = 5;

        // A TopK-honouring inner retriever: exactly TopK results per call, distinct per query so
        // the union cannot collapse under Deduplicate and hide the growth.
        inner
            .RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), ct)
            .Returns(ci =>
            {
                var q = ci.ArgAt<string>(0);
                var page = new SearchResult[topK];
                for (var i = 0; i < topK; i++)
                    page[i] = MakeResult($"{q}-doc{i}", i);

                return Ok(page);
            });

        ReturnInsufficientThenSufficient(chatClient, "s1", "s2", "s3");

        var sut = new DeepResearchRetriever(
            inner, chatClient, new DeepResearchOptions { SubQueryCount = 3 });

        var result = await sut.RetrieveAsync("q", new RetrievalOptions { TopK = topK }, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            topK,
            result.Value.Count);
    }

    /// <summary>
    /// The caller's own top hit survives truncation even when a sub-query returns chunks with
    /// higher raw scores. This is the half of #475 that truncation alone would have made worse.
    /// </summary>
    /// <remarks>
    /// Sorting the union by score and cutting at <c>TopK</c> would return <c>sub-a</c> and
    /// <c>sub-b</c> here — 0.99 and 0.98 against the primary's 0.50 and 0.40 — dropping the chunks
    /// the caller's actual question ranked first. Those scores come from a different query vector,
    /// so the comparison that produces that page is not a valid one. Rank fusion takes each
    /// ranking's leader instead.
    /// </remarks>
    [Fact]
    public async Task HigherScoringSubQueryChunks_DoNotDisplaceThePrimaryQuerysTopHit()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();

        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("primary-a", 0, 0.50), MakeResult("primary-b", 0, 0.40)));
        inner.RetrieveAsync("s1", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("sub-a", 0, 0.99), MakeResult("sub-b", 0, 0.98)));
        ReturnInsufficientThenSufficient(chatClient, "s1");

        var sut = new DeepResearchRetriever(
            inner, chatClient, new DeepResearchOptions { SubQueryCount = 1 });

        var result = await sut.RetrieveAsync("q", new RetrievalOptions { TopK = 2 }, ct);

        Assert.True(result.IsSuccess);
        var ids = result.Value.Select(r => r.Chunk.DocumentId.Value).ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Contains("primary-a", ids, StringComparer.Ordinal);
        Assert.Contains("sub-a", ids, StringComparer.Ordinal);
        Assert.DoesNotContain(
            "sub-b",
            ids,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// When nothing expands, the retriever is a pass-through and scores are left alone.
    /// </summary>
    /// <remarks>
    /// Fusing a single ranking with nothing is arithmetically harmless but would still rewrite
    /// every <see cref="SearchResult.Score"/> onto the RRF scale — turning 0.9 into 1/61 — for a
    /// call that added no information. Anything downstream reading the score would see a value it
    /// could not interpret, produced by a stage that did nothing.
    /// </remarks>
    [Fact]
    public async Task SufficientOnFirstPass_LeavesScoresOnTheInnerRetrieversScale()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = Substitute.For<IRetriever>();
        var chatClient = Substitute.For<IChatClient>();

        inner.RetrieveAsync("q", Arg.Any<RetrievalOptions?>(), ct)
            .Returns(Ok(MakeResult("doc1", 0, 0.9)));
        ReturnSufficient(chatClient);

        var sut = new DeepResearchRetriever(inner, chatClient, new DeepResearchOptions());
        var result = await sut.RetrieveAsync("q", new RetrievalOptions { TopK = 5 }, ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal(
            0.9,
            result.Value[0].Score);
    }
}
