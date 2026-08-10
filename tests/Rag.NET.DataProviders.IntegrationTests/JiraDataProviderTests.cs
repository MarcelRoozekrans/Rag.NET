using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Jira;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class JiraDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public JiraDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Jira", "https://test.atlassian.net");
    }

    private JiraDataProvider CreateProvider(JiraOptions? opts = null)
    {
        var services = new ServiceCollection();
        services.AddJiraDataProvider(
            baseUrl: _fixture.BaseUrl,
            email: "test@test.com",
            apiToken: "test-token",
            configure: opts is null ? null : o =>
            {
                if (!string.IsNullOrEmpty(opts.DeltaToken))
                    o.DeltaToken = opts.DeltaToken;
            });
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IFileContentProvider>() as JiraDataProvider
               ?? throw new InvalidOperationException("JiraDataProvider not registered");
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
            var request = entry.RequestMessage;
            Assert.NotNull(request);
            var headers = request.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Accept"), "Accept header missing");
            Assert.Contains("application/json", headers["Accept"], StringComparer.Ordinal);
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
            var request = entry.RequestMessage;
            Assert.NotNull(request);
            return request.RawQuery.Contains("updated", StringComparison.OrdinalIgnoreCase);
        });
    }
}
