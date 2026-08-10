using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Asana;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class AsanaDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public AsanaDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Asana", "https://app.asana.com");
    }

    private AsanaDataProvider CreateProvider(AsanaOptions? opts = null)
    {
        var services = new ServiceCollection();
        services.AddAsanaDataProvider(
            personalAccessToken: "fake-pat",
            workspaceGid: "ws-001",
            configure: opts is null ? null : o =>
            {
                if (!string.IsNullOrEmpty(opts.DeltaToken))
                    o.DeltaToken = opts.DeltaToken;
            },
            baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IFileContentProvider>() as AsanaDataProvider
               ?? throw new InvalidOperationException("AsanaDataProvider not registered");
    }

    [Fact]
    public async Task GetFilesAsync_GetTasks_YieldsTasks()
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
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "Design new login page.md", StringComparison.Ordinal));
        Assert.Contains(results, r => string.Equals(r.Value.FileName, "Fix payment bug.md", StringComparison.Ordinal));
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

        // Every request to the Asana API must carry an Accept: application/json header.
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
    public async Task GetFilesAsync_DeltaRun_UsesModifiedSince()
    {
        _fixture.LoadCassettes("Asana", "https://app.asana.com");
        _fixture.Server.ResetLogEntries();

        var opts = new AsanaOptions
        {
            WorkspaceGid = "ws-001",
            DeltaToken   = "2026-03-01T00:00:00Z",
        };
        var sut = CreateProvider(opts);

        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // At least one request to the tasks endpoint must carry the modified_since query param.
        Assert.Contains(logEntries, entry =>
        {
            var request = entry.RequestMessage;
            Assert.NotNull(request);
            return request.AbsolutePath.Contains("/api/1.0/tasks", StringComparison.Ordinal)
                && request.RawQuery.Contains("modified_since", StringComparison.Ordinal);
        });
    }
}
