using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.WebSearch.Tavily;
using ZeroAlloc.Results;
using Xunit;

namespace Rag.NET.WebSearch.Tavily.Tests;

public class TavilyWebSearchTests
{
    private static ITavilyApi MakeApiReturning(TavilySearchResponse response)
    {
        var api = Substitute.For<ITavilyApi>();
        Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError> result = response;
        api.SearchAsync(Arg.Any<TavilySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return api;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_MapsResultsToSearchResults()
    {
        var response = new TavilySearchResponse
        {
            Results =
            [
                new TavilyResult { Title = "Page 1", Url = "https://example.com/1", Content = "content one", Score = 0.9 },
                new TavilyResult { Title = "Page 2", Url = "https://example.com/2", Content = "content two", Score = 0.7 },
            ]
        };
        var api = MakeApiReturning(response);
        var sut = new TavilyWebSearch(api, "test-key");

        var results = await sut.SearchAsync("test query", topK: 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("content one", results[0].Chunk.Text);
        Assert.Equal("https://example.com/1", results[0].Chunk.DocumentId.Value);
        Assert.Equal(0.9, results[0].Score);
        Assert.Equal<MetadataValue>("tavily", results[0].Chunk.Metadata["source"]);
    }

    [Fact]
    public async Task SearchAsync_PassesApiKeyAndTopK()
    {
        var api = Substitute.For<ITavilyApi>();
        Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError> ok = new TavilySearchResponse { Results = [] };
        api.SearchAsync(Arg.Any<TavilySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ok));

        var sut = new TavilyWebSearch(api, "my-api-key");
        _ = await sut.SearchAsync("hello", topK: 3, TestContext.Current.CancellationToken);

        _ = await api.Received(1).SearchAsync(
            Arg.Is<TavilySearchRequest>(r =>
                string.Equals(r!.ApiKey, "my-api-key", StringComparison.Ordinal) &&
                string.Equals(r.Query, "hello", StringComparison.Ordinal) &&
                r.MaxResults == 3),
            Arg.Any<CancellationToken>());
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_HttpError_ThrowsHttpRequestException()
    {
        var api = Substitute.For<ITavilyApi>();
        Result<TavilySearchResponse, ZeroAlloc.Rest.HttpError> fail =
            new ZeroAlloc.Rest.HttpError(
                System.Net.HttpStatusCode.Unauthorized,
                System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>.Empty,
                "Unauthorized");
        api.SearchAsync(Arg.Any<TavilySearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fail));

        var sut = new TavilyWebSearch(api, "bad-key");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.SearchAsync("query", topK: 5, TestContext.Current.CancellationToken));
    }

    // ── DI registration ───────────────────────────────────────────────────────

    [Fact]
    public void AddTavilyWebSearch_RegistersIWebSearch()
    {
        var services = new ServiceCollection();
        services.AddTavilyWebSearch("test-key");
        var sp = services.BuildServiceProvider();

        var webSearch = sp.GetService<IWebSearch>();

        Assert.NotNull(webSearch);
        Assert.IsType<TavilyWebSearch>(webSearch);
    }
}
