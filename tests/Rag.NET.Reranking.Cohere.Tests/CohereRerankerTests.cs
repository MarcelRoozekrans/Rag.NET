using System.Text.Json;
using Rag.NET.Models;
using Rag.NET.Reranking.Cohere;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Rag.NET.Reranking.Cohere.Tests;

public sealed class CohereRerankerTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly CohereRerankerOptions _defaultOptions;

    public CohereRerankerTests()
    {
        _server = WireMockServer.Start();
        _defaultOptions = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            MaxDocumentsPerBatch = 1000,
            TopN = 100,
        };
    }

    public void Dispose() => _server.Stop();

    private CohereRerankerOptions CreateOptions(int maxDocumentsPerBatch = 1000) =>
        new()
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            MaxDocumentsPerBatch = maxDocumentsPerBatch,
            TopN = 100,
        };

    private static SearchResult MakeSearchResult(string text, int chunkIndex = 0) =>
        new()
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId("doc-1"),
                ChunkIndex = chunkIndex,
            },
            Score = 0.5,
        };

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CohereReranker(null!));
    }

    [Fact]
    public void Constructor_WhenApiKeyIsEmpty_ThrowsArgumentException()
    {
        var options = new CohereRerankerOptions { ApiKey = "" };
        Assert.Throws<ArgumentException>(() => new CohereReranker(options));
    }

    // -------------------------------------------------------------------------
    // RerankAsync – empty input
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenResultsEmpty_ReturnsEmptyWithoutCallingApi()
    {
        using var reranker = new CohereReranker(CreateOptions());

        var result = await reranker.RerankAsync("query", [], CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_server.LogEntries);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – single result
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_SingleResult_ReturnsMappedScore()
    {
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"x\",\"results\":[{\"index\":0,\"relevance_score\":0.95}]}"));

        using var reranker = new CohereReranker(CreateOptions());
        var doc = MakeSearchResult("hello world");

        var results = await reranker.RerankAsync("query", [doc], CancellationToken.None);

        Assert.Single(results);
        Assert.Same(doc, results[0].SearchResult);
        Assert.Equal(0.95, results[0].RelevanceScore, precision: 3);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – multiple results sorted descending
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_MultipleResults_ReturnsSortedDescending()
    {
        // Cohere returns index 1 at 0.9 and index 0 at 0.3
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"x\",\"results\":[{\"index\":1,\"relevance_score\":0.9},{\"index\":0,\"relevance_score\":0.3}]}"));

        using var reranker = new CohereReranker(CreateOptions());
        var doc0 = MakeSearchResult("first doc", chunkIndex: 0);
        var doc1 = MakeSearchResult("second doc", chunkIndex: 1);

        var results = await reranker.RerankAsync("query", [doc0, doc1], CancellationToken.None);

        Assert.Equal(2, results.Count);
        // Highest score first
        Assert.Equal(0.9, results[0].RelevanceScore, precision: 3);
        Assert.Same(doc1, results[0].SearchResult);
        Assert.Equal(0.3, results[1].RelevanceScore, precision: 3);
        Assert.Same(doc0, results[1].SearchResult);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – index mapping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_IndexMappingIsCorrect()
    {
        // Cohere returns only index 2 at 0.8
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"x\",\"results\":[{\"index\":2,\"relevance_score\":0.8}]}"));

        using var reranker = new CohereReranker(CreateOptions());
        var doc0 = MakeSearchResult("alpha", chunkIndex: 0);
        var doc1 = MakeSearchResult("beta", chunkIndex: 1);
        var doc2 = MakeSearchResult("gamma", chunkIndex: 2);

        var results = await reranker.RerankAsync("query", [doc0, doc1, doc2], CancellationToken.None);

        Assert.Single(results);
        Assert.Same(doc2, results[0].SearchResult);
        Assert.Equal(0.8, results[0].RelevanceScore, precision: 3);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – batching
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenBatchingRequired_MergesAndSorts()
    {
        // Batch 1: docs[0] and docs[1] — index 0 scores 0.7, index 1 scores 0.4
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("batching")
            .WillSetStateTo("batch2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"b1\",\"results\":[{\"index\":0,\"relevance_score\":0.7},{\"index\":1,\"relevance_score\":0.4}]}"));

        // Batch 2: docs[2] — index 0 (mapped to offset 2) scores 0.9
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("batching")
            .WhenStateIs("batch2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"id\":\"b2\",\"results\":[{\"index\":0,\"relevance_score\":0.9}]}"));

        using var reranker = new CohereReranker(CreateOptions(maxDocumentsPerBatch: 2));
        var doc0 = MakeSearchResult("doc zero", chunkIndex: 0);
        var doc1 = MakeSearchResult("doc one", chunkIndex: 1);
        var doc2 = MakeSearchResult("doc two", chunkIndex: 2);

        var results = await reranker.RerankAsync("query", [doc0, doc1, doc2], CancellationToken.None);

        Assert.Equal(3, results.Count);
        // Sorted descending: doc2(0.9), doc0(0.7), doc1(0.4)
        Assert.Same(doc2, results[0].SearchResult);
        Assert.Equal(0.9, results[0].RelevanceScore, precision: 3);
        Assert.Same(doc0, results[1].SearchResult);
        Assert.Equal(0.7, results[1].RelevanceScore, precision: 3);
        Assert.Same(doc1, results[2].SearchResult);
        Assert.Equal(0.4, results[2].RelevanceScore, precision: 3);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – cancellation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var reranker = new CohereReranker(CreateOptions());
        var doc = MakeSearchResult("some text");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reranker.RerankAsync("query", [doc], cts.Token));
    }

    // -------------------------------------------------------------------------
    // Constructor – whitespace ApiKey
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_WhenApiKeyIsWhitespace_ThrowsArgumentException()
    {
        var options = new CohereRerankerOptions { ApiKey = "   ", Endpoint = _server.Url };
        Assert.Throws<ArgumentException>(() => new CohereReranker(options));
    }

    // -------------------------------------------------------------------------
    // RerankAsync – null guard: query
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenQueryIsNull_ThrowsArgumentNullException()
    {
        using var reranker = new CohereReranker(_defaultOptions);
        var docs = new[] { MakeSearchResult("doc") };

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => reranker.RerankAsync(null!, docs, TestContext.Current.CancellationToken));
    }

    // -------------------------------------------------------------------------
    // RerankAsync – null guard: results
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenResultsIsNull_ThrowsArgumentNullException()
    {
        using var reranker = new CohereReranker(_defaultOptions);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => reranker.RerankAsync("query", null!, TestContext.Current.CancellationToken));
    }

    // -------------------------------------------------------------------------
    // RerankAsync – TopN cap applied after multi-batch merge
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_TopNCapAppliedAfterBatchMerge()
    {
        var opts = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            MaxDocumentsPerBatch = 2,
            TopN = 1,
        };

        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("topn-cap")
            .WillSetStateTo("batch2")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.5f }, new { index = 1, relevance_score = 0.4f } })));
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("topn-cap")
            .WhenStateIs("batch2")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.9f } })));

        var docs = new[]
        {
            MakeSearchResult("doc 0"),
            MakeSearchResult("doc 1"),
            MakeSearchResult("doc 2"),
        };
        using var reranker = new CohereReranker(opts);

        var results = await reranker.RerankAsync("query", docs, TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(docs[2], results[0].SearchResult); // score 0.9 wins
    }

    // -------------------------------------------------------------------------
    // RerankAsync – ReturnDocuments=true is sent on wire
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenReturnDocumentsTrue_SentInRequest()
    {
        var opts = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            ReturnDocuments = true,
        };

        _server
            .Given(Request.Create()
                .WithPath("/v1/rerank")
                .UsingPost()
                .WithBody(new WireMock.Matchers.JsonPartialMatcher("{\"return_documents\":true}")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.7f } })));

        using var reranker = new CohereReranker(opts);
        var results = await reranker.RerankAsync("query", new[] { MakeSearchResult("doc") }, TestContext.Current.CancellationToken);

        Assert.Single(results);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – Model option is sent on wire
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_ModelSentInRequest()
    {
        var opts = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            Model = "rerank-v3.5",
        };

        _server
            .Given(Request.Create()
                .WithPath("/v1/rerank")
                .UsingPost()
                .WithBody(new WireMock.Matchers.JsonPartialMatcher("{\"model\":\"rerank-v3.5\"}")))
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.8f } })));

        using var reranker = new CohereReranker(opts);
        var results = await reranker.RerankAsync("query", new[] { MakeSearchResult("doc") }, TestContext.Current.CancellationToken);

        Assert.Single(results);
    }

    // -------------------------------------------------------------------------
    // RerankAsync – MaxDocumentsPerBatch=1 edge case
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RerankAsync_WhenMaxDocumentsPerBatchIsOne_EachDocInOwnBatch()
    {
        var opts = new CohereRerankerOptions
        {
            ApiKey = "test-key",
            Endpoint = _server.Url,
            MaxDocumentsPerBatch = 1,
            TopN = 10,
        };

        // Each batch of 1 doc: Cohere returns index 0 with different scores
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("one-per-batch")
            .WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.3f } })));
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .InScenario("one-per-batch")
            .WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson(new[] { new { index = 0, relevance_score = 0.8f } })));

        var docs = new[]
        {
            MakeSearchResult("doc 0"),
            MakeSearchResult("doc 1"),
        };
        using var reranker = new CohereReranker(opts);

        var results = await reranker.RerankAsync("query", docs, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(docs[1], results[0].SearchResult); // 0.8 wins
        Assert.Equal(docs[0], results[1].SearchResult); // 0.3
    }

    // -------------------------------------------------------------------------
    // RerankAsync – TopN (issue #94)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The default caps nothing, so the reranker cannot decide the answer's size behind the
    /// caller's back. It defaulted to 5: a pipeline asking for TopK = 20 fetched 60 candidates,
    /// got 5 back, and its own Take(20) did nothing.
    /// </summary>
    [Fact]
    public void TopN_DefaultsToNoCap_SoTheRerankerDoesNotDecideTheResultCount()
    {
        Assert.Null(new CohereRerankerOptions { ApiKey = "test-key" }.TopN);
    }

    /// <summary>
    /// With no cap configured, every candidate comes back ranked and the caller truncates.
    /// </summary>
    [Fact]
    public async Task RerankAsync_WhenTopNIsNull_ReturnsEveryCandidate()
    {
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson([
                    new { index = 0, relevance_score = 0.9 },
                    new { index = 1, relevance_score = 0.8 },
                    new { index = 2, relevance_score = 0.7 },
                ])));

        var options = CreateOptions();
        options.TopN = null;
        using var reranker = new CohereReranker(options);

        var results = await reranker.RerankAsync(
            "query",
            [MakeSearchResult("a", 0), MakeSearchResult("b", 1), MakeSearchResult("c", 2)],
            TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
    }

    /// <summary>
    /// <c>top_n</c> is a per-call parameter, so sending it while batching asks each batch for its
    /// own top N and throws the rest away before the merge — a document ranked sixth inside one
    /// batch but third overall would never reach the merged ranking. When more than one call is
    /// needed the cap is applied once, afterwards, so it means what the caller thinks it means.
    /// </summary>
    [Fact]
    public async Task RerankAsync_WhenBatching_DoesNotSendTopNPerCall_SoTheMergeSeesEveryCandidate()
    {
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson([new { index = 0, relevance_score = 0.5 }])));

        var options = CreateOptions(maxDocumentsPerBatch: 2);
        options.TopN = 100;
        using var reranker = new CohereReranker(options);

        await reranker.RerankAsync(
            "query",
            [MakeSearchResult("a", 0), MakeSearchResult("b", 1), MakeSearchResult("c", 2)],
            TestContext.Current.CancellationToken);

        var bodies = _server.LogEntries
            .Select(entry => entry.RequestMessage?.Body ?? string.Empty)
            .ToList();

        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, body =>
            Assert.DoesNotContain("\"top_n\":1", body, StringComparison.Ordinal));
    }

    /// <summary>A single call covers every candidate, so the cap can be pushed to the server.</summary>
    [Fact]
    public async Task RerankAsync_WhenOneBatchCoversEverything_SendsTopN()
    {
        _server
            .Given(Request.Create().WithPath("/v1/rerank").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(RerankJson([new { index = 0, relevance_score = 0.5 }])));

        var options = CreateOptions();
        options.TopN = 7;
        using var reranker = new CohereReranker(options);

        await reranker.RerankAsync(
            "query", [MakeSearchResult("a", 0)], TestContext.Current.CancellationToken);

        var body = Assert.Single(_server.LogEntries).RequestMessage?.Body ?? string.Empty;
        Assert.Contains("7", body, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string RerankJson<T>(T[] items)
    {
        var results = JsonSerializer.Serialize(items);
        return $"{{\"id\":\"x\",\"results\":{results}}}";
    }
}
