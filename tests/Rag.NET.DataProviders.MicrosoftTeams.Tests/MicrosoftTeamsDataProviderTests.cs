using Microsoft.Graph;
using Rag.NET.DataProviders.MicrosoftTeams;
using Xunit;

namespace Rag.NET.DataProviders.MicrosoftTeams.Tests;

public sealed class MicrosoftTeamsDataProviderTests
{
    // -------------------------------------------------------------------------
    // Stub JSON payloads
    // -------------------------------------------------------------------------

    private const string JoinedTeamsJson = """
        {
          "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#teams",
          "value": [{ "id": "team-1", "displayName": "Engineering" }]
        }
        """;

    private const string ChannelsJson = """
        {
          "value": [{ "id": "chan-1", "displayName": "general" }]
        }
        """;

    private const string MessagesJson = """
        {
          "value": [{
            "id": "msg-1",
            "createdDateTime": "2026-03-01T10:00:00Z",
            "lastModifiedDateTime": "2026-03-01T10:00:00Z",
            "from": { "user": { "displayName": "Alice" } },
            "body": { "content": "Hello team", "contentType": "text" }
          }]
        }
        """;

    // URL substrings matched by the fake handler (Graph SDK sends /v1.0/... paths).
    // Longer keys take precedence over shorter ones (most-specific wins).
    private const string JoinedTeamsKey  = "/me/joinedTeams";
    private const string ChannelsKey     = "/team-1/channels";
    private const string MessagesKey     = "/team-1/channels/chan-1/messages";

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsOneEntry()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = ChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions();
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("general-2026-03-01.md", entries[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_TeamIdAndChannelIdPinned_YieldsOneEntry()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessagesKey] = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { TeamId = "team-1", ChannelId = "chan-1" };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("chan-1-2026-03-01.md", entries[0].FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JoinedTeamsKey] = JoinedTeamsJson,
            [ChannelsKey]    = ChannelsJson,
            [MessagesKey]    = MessagesJson,
        };
        var graph = MakeGraphClient(responses);
        var opts  = new MicrosoftTeamsOptions { Extensions = [".txt"] };
        var sut   = new MicrosoftTeamsDataProvider(graph, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MicrosoftTeamsDataProvider(null!, new MicrosoftTeamsOptions()));
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static GraphServiceClient MakeGraphClient(Dictionary<string, string> responses)
    {
        var handler    = new FakeGraphHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new GraphServiceClient(httpClient);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Intercepts outbound Graph SDK HTTP calls and returns canned JSON payloads.
/// Full-URL match is attempted first; then the longest matching substring key wins
/// (most specific key takes precedence over shorter, more general keys).
/// </summary>
file sealed class FakeGraphHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // Exact full-URL match first (e.g. delta tokens)
        if (responses.TryGetValue(url, out var exact))
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(exact, System.Text.Encoding.UTF8, "application/json"),
            });

        // Substring match — pick the longest matching key so more-specific routes win
        string? bestKey   = null;
        int     bestLen   = -1;
        foreach (var k in responses.Keys)
        {
            if (url.Contains(k, StringComparison.Ordinal) && k.Length > bestLen)
            {
                bestKey = k;
                bestLen = k.Length;
            }
        }

        if (bestKey is null)
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responses[bestKey], System.Text.Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Minimal stub to satisfy the <see cref="GraphServiceClient"/> constructor.</summary>
file sealed class FakeTokenCredential : Azure.Core.TokenCredential
{
    public override Azure.Core.AccessToken GetToken(
        Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new(string.Empty, DateTimeOffset.MaxValue);

    public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(
        Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromResult(new Azure.Core.AccessToken(string.Empty, DateTimeOffset.MaxValue));
}
