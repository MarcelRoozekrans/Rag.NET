using Azure;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

[Collection("AzureAISearch")]
public class AzureAISearchVectorStoreTests : IAsyncLifetime
{
    private readonly string? _endpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT");
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_API_KEY");
    private AzureAISearchVectorStore? _sut;
    private readonly string _indexName = $"ragnet-test-{Guid.NewGuid():N}"[..24];

    public async ValueTask InitializeAsync()
    {
        if (_endpoint is null || _apiKey is null)
        {
            return;
        }

        _sut = new AzureAISearchVectorStore(
            new Uri(_endpoint),
            _indexName,
            new AzureKeyCredential(_apiKey),
            vectorDimensions: 3);

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_endpoint is not null && _apiKey is not null)
        {
            var indexClient = new Azure.Search.Documents.Indexes.SearchIndexClient(
                new Uri(_endpoint), new AzureKeyCredential(_apiKey));

            try
            {
                await indexClient.DeleteIndexAsync(_indexName, TestContext.Current.CancellationToken);
            }
            catch (Azure.RequestFailedException)
            {
                // Best effort cleanup — index may not exist
            }
        }
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        Assert.SkipWhen(_sut is null, "AZURE_SEARCH_ENDPOINT and AZURE_SEARCH_API_KEY not set");

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

        await _sut!.StoreAsync(chunks, TestContext.Current.CancellationToken);

        // Azure AI Search indexing is near real-time; wait for consistency
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
        Assert.SkipWhen(_sut is null, "AZURE_SEARCH_ENDPOINT and AZURE_SEARCH_API_KEY not set");

        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = "doc-to-delete", ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        await _sut!.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await _sut.DeleteByDocumentIdAsync("doc-to-delete", TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        Assert.SkipWhen(_sut is null, "AZURE_SEARCH_ENDPOINT and AZURE_SEARCH_API_KEY not set");

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

        await _sut!.StoreAsync(chunks, TestContext.Current.CancellationToken);

        // Azure AI Search indexing is near real-time; wait for consistency
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
}
