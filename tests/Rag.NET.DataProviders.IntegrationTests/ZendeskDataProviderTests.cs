using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Zendesk;
using Rag.NET.Testing;
using WireMock;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class ZendeskDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    /// <summary>
    /// WireMock.Net v2 made <see cref="ILogEntry.RequestMessage"/> nullable. A captured log
    /// entry with no request message means the test's premise is broken, so this asserts
    /// loudly rather than letting callers dereference a possibly-null reference.
    /// </summary>
    private static IRequestMessage RequireRequest(ILogEntry entry)
    {
        var request = entry.RequestMessage;
        Assert.NotNull(request);
        return request;
    }

    public ZendeskDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Zendesk", "https://test.zendesk.com");
    }

    private (ZendeskTicketsDataProvider Tickets, ZendeskArticlesDataProvider Articles) CreateProviders()
    {
        var services = new ServiceCollection();
        services.AddZendeskTicketsDataProvider(
            subdomain: "test",
            email: "a@b.com",
            apiToken: "test-token",
            baseUrl: _fixture.BaseUrl);
        services.AddZendeskArticlesDataProvider(
            subdomain: "test",
            email: "a@b.com",
            apiToken: "test-token",
            baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        var all = sp.GetServices<IFileContentProvider>().ToList();
        var tickets = all.OfType<ZendeskTicketsDataProvider>().FirstOrDefault()
                      ?? throw new InvalidOperationException("ZendeskTicketsDataProvider not registered");
        var articles = all.OfType<ZendeskArticlesDataProvider>().FirstOrDefault()
                       ?? throw new InvalidOperationException("ZendeskArticlesDataProvider not registered");
        return (tickets, articles);
    }

    [Fact]
    public async Task GetTickets_YieldsTickets()
    {
        var (sut, _) = CreateProviders();

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

        var (sut, _) = CreateProviders();

        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // Every request to the Zendesk API must carry an Accept: application/json header.
        Assert.All(logEntries, entry =>
        {
            var headers = RequireRequest(entry).Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Accept"), "Accept header missing");
            Assert.Contains("application/json", headers["Accept"], StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GetArticles_YieldsArticles()
    {
        var (_, sut) = CreateProviders();

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

    [Fact]
    public async Task GetTickets_IncrementalCursor_Followed()
    {
        _fixture.LoadCassettes("Zendesk", "https://test.zendesk.com");

        const string scenario = "zendesk-cursor";
        const string page2State = "page-2";

        // Page 1: returns a cursor; transitions scenario to page-2.
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/api/v2/incremental/tickets/cursor.json")
                .UsingGet())
            .InScenario(scenario)
            .WillSetStateTo(page2State)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody("""{"tickets":[],"after_cursor":"cursor123","end_of_stream":false,"end_time":0}"""));

        // Page 2: matches only after state transition; returns end-of-stream.
        _fixture.Server
            .Given(Request.Create()
                .WithPath("/api/v2/incremental/tickets/cursor.json")
                .UsingGet())
            .InScenario(scenario)
            .WhenStateIs(page2State)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody("""{"tickets":[],"after_cursor":null,"end_of_stream":true,"end_time":0}"""));

        _fixture.Server.ResetLogEntries();

        var (sut, _) = CreateProviders();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);

        var ticketRequests = _fixture.Server.LogEntries
            .Where(e => RequireRequest(e).AbsolutePath.Contains(
                "/api/v2/incremental/tickets/cursor.json", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, ticketRequests.Count);
        Assert.Contains(ticketRequests, e =>
            !RequireRequest(e).RawQuery.Contains("cursor=", StringComparison.Ordinal));
        Assert.Contains(ticketRequests, e =>
            RequireRequest(e).RawQuery.Contains("cursor=cursor123", StringComparison.Ordinal));
    }
}
