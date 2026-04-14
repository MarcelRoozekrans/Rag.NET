using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Rag.NET.DataProviders.MicrosoftTeams;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class MicrosoftTeamsDataProviderTests
{
    private readonly WireMockServerFixture _fixture;

    public MicrosoftTeamsDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("MicrosoftTeams", "https://graph.microsoft.com");
    }

    private MicrosoftTeamsDataProvider CreateProvider(MicrosoftTeamsOptions? opts = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        var graph = new GraphServiceClient(
            http,
            new AnonymousAuthenticationProvider(),
            _fixture.BaseUrl + "/v1.0");
        return new MicrosoftTeamsDataProvider(graph, opts ?? new MicrosoftTeamsOptions());
    }

    [Fact]
    public async Task GetMessages_YieldsMessages()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.NotEmpty(r.Value.FileName);
            Assert.NotEmpty(r.Value.Id.Value);
        });
        // FileName format: {channelName}-{yyyy-MM-dd}.md
        Assert.Contains(results, r => r.Value.FileName.StartsWith("general-", StringComparison.Ordinal)
                                      && r.Value.FileName.EndsWith(".md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullTraversal_ReturnsNonEmptyStream()
    {
        var sut = CreateProvider();

        var results = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
        await Assert.AllAsync(results, async r =>
        {
            Assert.True(r.IsSuccess);
            await using var stream = await r.Value.OpenContentAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(stream);
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, TestContext.Current.CancellationToken);
            Assert.True(ms.Length > 0);
        });
    }
}
