using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Slack;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class SlackDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public SlackDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Slack", "https://slack.com");
    }

    private SlackDataProvider CreateProvider(SlackOptions? opts = null)
    {
        var services = new ServiceCollection();
        services.AddSlackDataProvider(
            botToken: "xoxb-test",
            configure: opts is null ? null : o =>
            {
                if (!string.IsNullOrEmpty(opts.DeltaToken))
                    o.DeltaToken = opts.DeltaToken;
            },
            baseUrl: _fixture.BaseUrl);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IFileContentProvider>() as SlackDataProvider
               ?? throw new InvalidOperationException("SlackDataProvider not registered");
    }

    [Fact]
    public async Task GetFilesAsync_ListChannels_YieldsMessages()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // 2 channels × 1 day each = 2 files
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.FileName);
            Assert.NotEmpty(r.Value.Id.Value);
        });
        Assert.Contains(results, r => r.Value.FileName.StartsWith("general-", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Value.FileName.StartsWith("random-", StringComparison.Ordinal));
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

        // Every request to the Slack API must carry an Accept: application/json header.
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
    public async Task GetFilesAsync_DeltaRun_UsesOldestParam()
    {
        _fixture.LoadCassettes("Slack", "https://slack.com");
        _fixture.Server.ResetLogEntries();

        const string deltaToken = "1711929600.000000";
        var opts = new SlackOptions { DeltaToken = deltaToken };
        var sut = CreateProvider(opts);

        await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var logEntries = _fixture.Server.LogEntries.ToList();

        // At least one conversations.history call must carry the oldest query parameter.
        Assert.Contains(logEntries, entry =>
        {
            var request = entry.RequestMessage;
            Assert.NotNull(request);
            return request.AbsolutePath.Contains("/api/conversations.history", StringComparison.Ordinal)
                && request.RawQuery.Contains("oldest=", StringComparison.Ordinal);
        });
    }
}
