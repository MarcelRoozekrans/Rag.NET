using System.Net.Http.Headers;
using Rag.NET.DataProviders.Zendesk;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class ZendeskDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public ZendeskDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Zendesk", "https://test.zendesk.com");
    }

    private ZendeskApiClient CreateApiClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        return new ZendeskApiClient(http, JsonSerializer);
    }

    [Fact]
    public async Task GetTickets_YieldsTickets()
    {
        var sut = new ZendeskTicketsDataProvider(
            CreateApiClient(),
            new ZendeskTicketsOptions { Subdomain = "test", Email = "a@b.com" });

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.FileName);
            Assert.NotEmpty(r.Value.Id.Value);
        });
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "ticket-1.md", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "ticket-2.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTickets_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        var sut = new ZendeskTicketsDataProvider(
            CreateApiClient(),
            new ZendeskTicketsOptions { Subdomain = "test", Email = "a@b.com" });

        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // Every request to the Zendesk API must carry an Accept: application/json header.
        Assert.All(logEntries, entry =>
        {
            var headers = entry.RequestMessage.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Accept"), "Accept header missing");
            Assert.Contains("application/json", headers["Accept"]);
        });
    }

    [Fact]
    public async Task GetArticles_YieldsArticles()
    {
        var sut = new ZendeskArticlesDataProvider(
            CreateApiClient(),
            new ZendeskArticlesOptions { Subdomain = "test", Email = "a@b.com" });

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.FileName);
            Assert.NotEmpty(r.Value.Id.Value);
        });
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "article-201.md", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "article-202.md", StringComparison.Ordinal));
    }
}
