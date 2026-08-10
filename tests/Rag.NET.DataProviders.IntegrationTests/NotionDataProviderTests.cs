using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Notion;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class NotionDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public NotionDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Notion", "https://api.notion.com");
    }

    private NotionDataProvider CreateProvider(NotionOptions? opts = null)
    {
        var services = new ServiceCollection();
        services.AddNotionDataProvider(
            integrationToken: "secret_test",
            baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IFileContentProvider>() as NotionDataProvider
               ?? throw new InvalidOperationException("NotionDataProvider not registered");
    }

    [Fact]
    public async Task GetFilesAsync_Search_YieldsPages()
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
        Assert.Contains(results, r =>
            string.Equals(r.Value.FileName, "Getting Started.md", StringComparison.Ordinal));
        Assert.Contains(results, r =>
            string.Equals(r.Value.FileName, "API Reference.md", StringComparison.Ordinal));
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

        // Every request to the Notion API must carry an Accept: application/json header.
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
    public async Task GetFilesAsync_FullTraversal_NotionVersionHeaderSent()
    {
        _fixture.Server.ResetLogEntries();

        var sut = CreateProvider();
        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();
        Assert.NotEmpty(logEntries);

        // Every request must carry the required Notion-Version header.
        Assert.All(logEntries, entry =>
        {
            var request = entry.RequestMessage;
            Assert.NotNull(request);
            var headers = request.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Notion-Version"), "Notion-Version header missing");
            Assert.Contains("2022-06-28", headers["Notion-Version"], StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GetFilesAsync_EachEntry_HasETag()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.ETag!);
        });
        Assert.Contains(results, r =>
            string.Equals(r.Value.ETag, "2026-03-10T10:00:00.000Z", StringComparison.Ordinal));
        Assert.Contains(results, r =>
            string.Equals(r.Value.ETag, "2026-03-12T14:00:00.000Z", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_GetBlocks_ReturnsContent()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.True(first.IsSuccess);

        await using var stream = await first.Value.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Hello from Notion.", content, StringComparison.Ordinal);
    }
}
