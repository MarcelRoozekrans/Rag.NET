using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Testing;
using Rag.NET.Models;
using Rag.NET.WebSearch.Tavily;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class TavilyWebSearchIntegrationTests
{
    private readonly WireMockServerFixture _fixture;

    public TavilyWebSearchIntegrationTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Tavily", "https://api.tavily.com");
    }

    private IWebSearch CreateWebSearch()
    {
        var services = new ServiceCollection();
        services.AddTavilyWebSearch("test-key", baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IWebSearch>();
    }

    [Fact]
    public async Task SearchAsync_YieldsResults()
    {
        var sut = CreateWebSearch();

        var results = await sut.SearchAsync(
            "retrieval augmented generation",
            topK: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.NotEmpty(r.Chunk.Text);
            Assert.NotEmpty(r.Chunk.DocumentId.Value);
        });
        Assert.Contains(results, r =>
            r.Chunk.DocumentId.Value.Equals("https://example.com/rag", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_ResultsHaveTavilyMetadata()
    {
        var sut = CreateWebSearch();

        var results = await sut.SearchAsync("test", topK: 2, TestContext.Current.CancellationToken);

        Assert.All(results, r =>
        {
            Assert.NotNull(r.Chunk.Metadata);
            Assert.Equal<MetadataValue>("tavily", r.Chunk.Metadata["source"]);
            Assert.True(r.Chunk.Metadata.ContainsKey("title"));
            Assert.True(r.Chunk.Metadata.ContainsKey("url"));
        });
    }
}
