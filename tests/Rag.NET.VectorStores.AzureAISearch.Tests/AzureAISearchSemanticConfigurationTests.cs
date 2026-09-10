using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents.Indexes;
using AzureSearchClientOptions = Azure.Search.Documents.SearchClientOptions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

/// <summary>
/// The ranker needs a semantic configuration on the index, and the store owns its name rather than
/// exposing it — one configuration, built from the fields the store already defines (#328).
/// </summary>
[Collection("AzureAISearch")]
public class AzureAISearchSemanticConfigurationTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithPortBinding(8443, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
        .Build();

    private Uri _endpoint = null!;
    private AzureKeyCredential _credential = null!;
    private AzureSearchClientOptions _clientOptions = null!;

    public async ValueTask InitializeAsync()
    {
        await _simulator.StartAsync(TestContext.Current.CancellationToken);
        var httpsPort = _simulator.GetMappedPublicPort(8443);

        var httpHandler = new HttpClientHandler
        {
#pragma warning disable MA0039 // Do not write your own certificate validation method — intentional for local test simulator
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
#pragma warning restore MA0039
        };

        _endpoint = new Uri($"https://localhost:{httpsPort}");
        _credential = new AzureKeyCredential("admin-key-12345");
        _clientOptions = new AzureSearchClientOptions { Transport = new HttpClientTransport(httpHandler) };
    }

    public async ValueTask DisposeAsync()
    {
        await _simulator.DisposeAsync();
    }

    /// <summary>
    /// The ranker needs a semantic configuration on the index, and the store owns its name rather
    /// than exposing it — one configuration, built from the fields the store already defines.
    /// </summary>
    [Fact]
    public async Task EnablingTheRanker_AddsASemanticConfigurationToTheIndex()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            new AzureAISearchOptions { EnableSemanticRanking = true });

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var indexClient = new SearchIndexClient(_endpoint, _credential, _clientOptions);
        var index = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        Assert.NotNull(index.Value.SemanticSearch);
        var configuration = Assert.Single(index.Value.SemanticSearch!.Configurations);
        var contentField = Assert.Single(configuration.PrioritizedFields.ContentFields);
        Assert.Equal("text", contentField.FieldName);
    }

    /// <summary>
    /// And an index built without the ranker carries none: a configuration nothing uses is
    /// clutter, and adding it unconditionally would rewrite every existing index for no benefit.
    /// </summary>
    [Fact]
    public async Task WithoutTheRanker_TheIndexHasNoSemanticConfiguration()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            options: null);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var indexClient = new SearchIndexClient(_endpoint, _credential, _clientOptions);
        var index = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        // Present-but-empty is not the same as absent: a future edit that unconditionally sets
        // index.SemanticSearch = new SemanticSearch() would rewrite every existing index on the
        // next initialisation, and this must fail if that happens.
        Assert.Null(index.Value.SemanticSearch);
    }

    /// <summary>
    /// <b>The simulator accepts semantic ranking and does not perform it</b>, which is exactly the
    /// failure this guard exists for. Measured 2026-09-09: it takes a semantic index configuration
    /// (HTTP 201) and a semantic query (HTTP 200 with results) and returns no rerankerScore at all.
    /// A real service does the same when the tier or region lacks the ranker, or when the
    /// configuration name does not match — every one of those a silent downgrade to ordinary
    /// scoring, with the caller believing results were reranked.
    /// </summary>
    /// <remarks>
    /// This test therefore asserts the guard against a service that genuinely does not rank, rather
    /// than against a mock. It is the strongest evidence available without a billable Azure
    /// resource, and it is why the feature can ship at all.
    /// </remarks>
    [Fact]
    public async Task RequestingTheRankerFromAServiceThatDoesNotRank_ThrowsOnTheHybridPath()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            new AzureAISearchOptions { EnableSemanticRanking = true });

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var docId = $"ais-{Guid.CreateVersion7():N}";
        await sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "semantic ranking candidate",
                        DocumentId = new DocumentId(docId),
                        ChunkIndex = 0,
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        // Poll rather than sleep a fixed guess, and poll on the throw itself. The guard fires
        // inside the result loop, so it only fires once the chunk is searchable: a fixed delay that
        // expired early would return an empty page, throw nothing, and fail this test reporting the
        // guard as broken when the real cause was indexing latency. See SearchIndexSettle.
        InvalidOperationException? exception = null;
        await SearchIndexSettle.WaitUntilAsync(
            "the stored chunk is searchable and the missing reranker score is caught",
            async () =>
            {
                try
                {
                    await sut.HybridSearchAsync(
                        "semantic ranking candidate",
                        new float[] { 1.0f, 0.0f, 0.0f },
                        new SearchOptions { TopK = 1 },
                        TestContext.Current.CancellationToken);
                    return false;
                }
                catch (InvalidOperationException caught)
                {
                    exception = caught;
                    return true;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(exception);
        Assert.Contains(indexName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the ranker enabled, the dense path returns ordinary cosine similarities and does not
    /// throw. Semantic ranking needs query text and <c>IVectorStore.SearchAsync</c> takes an
    /// embedding and a <c>SearchOptions</c> of <c>TopK</c>/<c>MinScore</c>/<c>MetadataFilter</c> —
    /// no text, by interface contract — so the ranker cannot run here on any tier in any region
    /// (#539). Before 6.2.36 this call threw; the throw was correct about the service and wrong
    /// about the path.
    /// </summary>
    [Fact]
    public async Task WithTheRankerEnabled_TheDensePathStillReturnsOrdinaryScores()
    {
        var indexName = $"ragnet-sem-{Guid.CreateVersion7():N}"[..24];
        using var sut = new AzureAISearchVectorStore(
            _endpoint,
            indexName,
            _credential,
            vectorDimensions: 3,
            _clientOptions,
            new AzureAISearchOptions { EnableSemanticRanking = true });

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var docId = $"ais-{Guid.CreateVersion7():N}";
        await sut.StoreAsync(
            [
                new EmbeddedChunk
                {
                    Chunk = new TextChunk
                    {
                        Text = "dense path candidate",
                        DocumentId = new DocumentId(docId),
                        ChunkIndex = 0,
                    },
                    Embedding = new float[] { 1.0f, 0.0f, 0.0f },
                },
            ],
            TestContext.Current.CancellationToken);

        IReadOnlyList<SearchResult> results = [];
        await SearchIndexSettle.WaitUntilAsync(
            "the stored chunk is searchable on the dense path",
            async () =>
            {
                results = await sut.SearchAsync(
                    new float[] { 1.0f, 0.0f, 0.0f },
                    new SearchOptions { TopK = 1 },
                    TestContext.Current.CancellationToken);
                return results.Count > 0;
            },
            TestContext.Current.CancellationToken);

        // The assertion has to distinguish a cosine similarity from a reranker score without
        // pinning the simulator's scoring formula. Azure's reranker score is roughly 0-4 and a
        // cosine similarity here is bounded by 1, so the range is the discriminator.
        var only = Assert.Single(results);
        Assert.InRange(only.Score, 0.0, 1.0);
    }
}
