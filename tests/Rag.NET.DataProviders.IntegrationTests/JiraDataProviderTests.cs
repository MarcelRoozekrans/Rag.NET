using System.Net.Http.Headers;
using Rag.NET.DataProviders.Jira;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class JiraDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public JiraDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Jira", "https://test.atlassian.net");
    }

    private JiraDataProvider CreateProvider(JiraOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        var api = new JiraApiClient(http, JsonSerializer);
        return new JiraDataProvider(api, opts ?? new JiraOptions
        {
            BaseUrl = _fixture.BaseUrl,
            Email   = "test@test.com",
        });
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsIssues()
    {
        var sut = CreateProvider();

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
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "PROJ-1.md", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "PROJ-2.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_AcceptsJsonHeader()
    {
        _fixture.Server.ResetLogEntries();

        var sut = CreateProvider();
        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // Every request to the Jira API must carry an Accept: application/json header.
        Assert.All(logEntries, entry =>
        {
            var headers = entry.RequestMessage.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Accept"), "Accept header missing");
            Assert.Contains("application/json", headers["Accept"]);
        });
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UsesUpdatedFilter()
    {
        _fixture.LoadCassettes("Jira", "https://test.atlassian.net");

        _fixture.Server.ResetLogEntries();

        var opts = new JiraOptions
        {
            BaseUrl    = _fixture.BaseUrl,
            Email      = "test@test.com",
            DeltaToken = "2026-03-01T00:00:00Z",
        };
        var sut = CreateProvider(opts);

        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // The JQL query sent to the Jira search endpoint must contain an "updated" filter.
        Assert.Contains(logEntries, entry =>
        {
            var query = entry.RequestMessage.RawQuery ?? string.Empty;
            return query.Contains("updated", StringComparison.OrdinalIgnoreCase);
        });
    }
}
