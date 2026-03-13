using Azure;
using Azure.Core.Pipeline;
using AzureSearchClientOptions = Azure.Search.Documents.SearchClientOptions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

[Collection("AzureAISearch")]
public class AzureAISearchVectorStoreTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithPortBinding(8443, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080)))
        .Build();

    private AzureAISearchVectorStore _sut = null!;
    private readonly string _indexName = $"ragnet-test-{Guid.NewGuid():N}"[..24];

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

        var options = new AzureSearchClientOptions
        {
            Transport = new HttpClientTransport(httpHandler),
        };

        _sut = new AzureAISearchVectorStore(
            new Uri($"https://localhost:{httpsPort}"),
            _indexName,
            new AzureKeyCredential("admin-key-12345"),
            vectorDimensions: 3,
            clientOptions: options);

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _simulator.DisposeAsync();
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = "doc-1", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = "doc-1", ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 1 },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("cats are great", results[0].Chunk.Text);
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = "doc-to-delete", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact(Skip = "azure-ai-search-simulator does not implement OData filter expressions")]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc", DocumentId = "doc-filter-1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = "doc-filter-2", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "marketing" },
                },
                Embedding = new float[] { 0.9f, 0.1f, 0.0f },
            },
        };

        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions
            {
                TopK = 10,
                MetadataFilter = new Dictionary<string, string>(StringComparer.Ordinal) { ["department"] = "engineering" },
            },
            TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("engineering doc", results[0].Chunk.Text);
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = (ICollectionManageable)_sut;
        var tempIndex = $"temp-{Guid.NewGuid():N}"[..24];

        await manageable.CreateCollectionAsync(tempIndex, 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync(tempIndex, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));
    }
}
