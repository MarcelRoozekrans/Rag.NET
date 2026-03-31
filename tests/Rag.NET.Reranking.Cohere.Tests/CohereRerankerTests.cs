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

    public CohereRerankerTests()
    {
        _server = WireMockServer.Start();
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
}
