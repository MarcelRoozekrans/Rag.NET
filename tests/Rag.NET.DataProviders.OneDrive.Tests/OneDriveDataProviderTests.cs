using Microsoft.Graph;
using Rag.NET.DataProviders.OneDrive;
using Xunit;

namespace Rag.NET.DataProviders.OneDrive.Tests;

public sealed class OneDriveDataProviderTests
{
    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        var opts = new OneDriveOptions { UserId = "me" };
        Assert.Throws<ArgumentNullException>(() =>
            new OneDriveDataProvider(null!, opts));
    }

    [Fact]
    public void Constructor_ValidArgs_Succeeds()
    {
        var graph = new GraphServiceClient(new HttpClient(), new FakeTokenCredential());
        var opts = new OneDriveOptions { UserId = "me" };
        var sut = new OneDriveDataProvider(graph, opts);
        Assert.NotNull(sut);
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsFiles()
    {
        // Arrange — two files and one folder (folder must be skipped)
        const string userId = "user-1";
        const string driveId = "drive-abc";

        var driveJson = $$$"""{ "id": "{{{driveId}}}" }""";
        var childrenJson = """
            {
              "value": [
                { "id": "file-1", "name": "readme.md",  "file": {}, "eTag": "etag-1" },
                { "id": "file-2", "name": "notes.txt",  "file": {}, "eTag": "etag-2" },
                { "id": "dir-1",  "name": "my-folder"                                 }
              ]
            }
            """;

        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"/users/{userId}/drive"]                 = driveJson,
            [$"/drives/{driveId}/items/root/children"] = childrenJson,
        };
        var graph = MakeGraphClient(responses);
        var opts = new OneDriveOptions { UserId = userId };
        var sut = new OneDriveDataProvider(graph, opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert — folder entry must be excluded; both file entries must be present
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id.Contains("readme.md", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Id.Contains("notes.txt", StringComparison.Ordinal));
        Assert.Equal("readme.md", entries.Single(e => e.Id.Contains("readme.md", StringComparison.Ordinal)).FileName);
        Assert.Equal("etag-1",    entries.Single(e => e.Id.Contains("readme.md", StringComparison.Ordinal)).ETag);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_YieldsOnlyChangedFiles()
    {
        // Arrange — delta URL returns exactly 1 changed file
        const string userId  = "user-1";
        const string driveId = "drive-abc";
        const string deltaUrl =
            "https://graph.microsoft.com/v1.0/drives/drive-abc/items/root/delta?token=tok1";

        var driveJson = $$$"""{ "id": "{{{driveId}}}" }""";
        var deltaJson = """
            {
              "value": [
                { "id": "file-changed", "name": "updated.md", "file": {}, "eTag": "etag-new" }
              ],
              "@odata.deltaLink": "https://graph.microsoft.com/v1.0/drives/drive-abc/items/root/delta?token=tok2"
            }
            """;

        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"/users/{userId}/drive"] = driveJson,
            [deltaUrl]                      = deltaJson,
        };
        var graph = MakeGraphClient(responses);
        var opts = new OneDriveOptions { UserId = userId, DeltaToken = deltaUrl };
        var sut = new OneDriveDataProvider(graph, opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(entries);
        Assert.Equal("updated.md", entries[0].FileName);
        Assert.Equal("etag-new",   entries[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        // Arrange — return .md and .yaml; filter keeps only .md
        const string userId  = "user-1";
        const string driveId = "drive-abc";

        var driveJson = $$$"""{ "id": "{{{driveId}}}" }""";
        var childrenJson = """
            {
              "value": [
                { "id": "file-1", "name": "guide.md",   "file": {}, "eTag": "etag-1" },
                { "id": "file-2", "name": "build.yaml", "file": {}, "eTag": "etag-2" }
              ]
            }
            """;

        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"/users/{userId}/drive"]                 = driveJson,
            [$"/drives/{driveId}/items/root/children"] = childrenJson,
        };
        var graph = MakeGraphClient(responses);
        var opts = new OneDriveOptions { UserId = userId, Extensions = [".md"] };
        var sut = new OneDriveDataProvider(graph, opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert — only the .md file survives
        Assert.Single(entries);
        Assert.Equal("guide.md", entries[0].FileName);
        Assert.DoesNotContain(entries, e => e.FileName.Contains(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Helper — builds a GraphServiceClient backed by a fake HTTP handler
    // -------------------------------------------------------------------------

    private static GraphServiceClient MakeGraphClient(Dictionary<string, string> responses)
    {
        var handler = new FakeGraphHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new GraphServiceClient(httpClient);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Intercepts outbound Graph SDK HTTP calls and returns canned JSON payloads.
/// Matching is done against the full URL first, then by path substring.
/// </summary>
file sealed class FakeGraphHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var key = responses.Keys.FirstOrDefault(k => string.Equals(url, k, StringComparison.Ordinal))
               ?? responses.Keys.FirstOrDefault(k => url.Contains(k, StringComparison.Ordinal));

        if (key is null)
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], System.Text.Encoding.UTF8, "application/json"),
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
