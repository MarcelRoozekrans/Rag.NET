using System.Net.Http.Headers;
using Rag.NET.DataProviders.Slack;
using Rag.NET.Testing;
using Xunit;
using ZeroAlloc.Rest.SystemTextJson;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class SlackDataProviderTests
{
    private static readonly SystemTextJsonSerializer JsonSerializer = new();
    private readonly WireMockServerFixture _fixture;

    public SlackDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("Slack", "https://slack.com");
    }

    private SlackDataProvider CreateProvider(SlackOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "xoxb-test");
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        var api = new SlackApiClient(http, JsonSerializer);
        return new SlackDataProvider(api, opts ?? new SlackOptions());
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
            var headers = entry.RequestMessage.Headers;
            Assert.NotNull(headers);
            Assert.True(headers.ContainsKey("Accept"), "Accept header missing");
            Assert.Contains("application/json", headers["Accept"]);
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
            entry.RequestMessage.AbsolutePath.Contains("/api/conversations.history", StringComparison.Ordinal)
            && entry.RequestMessage.RawQuery != null
            && entry.RequestMessage.RawQuery.Contains("oldest=", StringComparison.Ordinal));
    }
}
