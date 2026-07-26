using System.Net;
using Microsoft.Graph;
using Rag.NET.DataProviders.SharePoint;
using Xunit;

namespace Rag.NET.DataProviders.SharePoint.Tests;

public sealed class SharePointDataProviderTests
{
    [Fact]
    public void Constructor_NullGraph_Throws()
    {
        var opts = new SharePointOptions { SiteId = "s1", DriveId = "d1" };
        Assert.Throws<ArgumentNullException>(() =>
            new SharePointDataProvider(null!, opts));
    }

    [Fact]
    public void Constructor_ValidArgs_Succeeds()
    {
        var graph = new GraphServiceClient(new HttpClient(), new FakeTokenCredential());
        var opts = new SharePointOptions { SiteId = "s1", DriveId = "d1" };
        var sut = new SharePointDataProvider(graph, opts);
        Assert.NotNull(sut);
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsFiles()
    {
        // Arrange — two files and one folder (folder must be skipped)
        const string driveId = "drive-1";
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
            [$"/drives/{driveId}/items/root/children"] = childrenJson,
        };
        var graph = MakeGraphClient(responses);
        var opts = new SharePointOptions { SiteId = "site-1", DriveId = driveId };
        var sut = new SharePointDataProvider(graph, opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert — folder entry must be excluded; both file entries must be present
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "/readme.md", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "/notes.txt", StringComparison.Ordinal));
        Assert.Equal("readme.md", entries.Single(e => e.Value.Id.Value.Contains("readme.md", StringComparison.Ordinal)).Value.FileName);
        Assert.Equal("etag-1",    entries.Single(e => e.Value.Id.Value.Contains("readme.md", StringComparison.Ordinal)).Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_YieldsOnlyChangedFiles()
    {
        // Arrange — delta URL returns exactly 1 changed file
        const string driveId = "drive-1";
        const string deltaUrl = "https://graph.microsoft.com/v1.0/drives/drive-1/items/root/delta?token=tok1";
        var deltaJson = """
            {
              "value": [
                { "id": "file-changed", "name": "updated.md", "file": {}, "eTag": "etag-new" }
              ],
              "@odata.deltaLink": "https://graph.microsoft.com/v1.0/drives/drive-1/items/root/delta?token=tok2"
            }
            """;

        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [deltaUrl] = deltaJson,
        };
        var graph = MakeGraphClient(responses);
        var opts = new SharePointOptions { SiteId = "site-1", DriveId = driveId, DeltaToken = deltaUrl };
        var sut = new SharePointDataProvider(graph, opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        _ = Assert.Single(entries);
        Assert.Equal("updated.md", entries[0].Value.FileName);
        Assert.Equal("etag-new",   entries[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        // Arrange — return .md and .yaml; filter keeps only .md
        const string driveId = "drive-1";
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
            [$"/drives/{driveId}/items/root/children"] = childrenJson,
        };
        var graph = MakeGraphClient(responses);
        var opts = new SharePointOptions { SiteId = "site-1", DriveId = driveId, Extensions = [".md"] };
        var sut = new SharePointDataProvider(graph, opts);

        // Act
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Assert — only the .md file survives
        _ = Assert.Single(entries);
        Assert.Equal("guide.md", entries[0].Value.FileName);
        Assert.DoesNotContain(entries, e => e.Value.FileName.Contains(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------------------
    // Transport failures (no HTTP response at all) → RagError.TransportFailed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_TransportFailure_YieldsTransportFailedResult()
    {
        // Before this mapping the HttpRequestException escaped the Result channel entirely.
        var graph = MakeThrowingGraphClient(
            () => new HttpRequestException("No such host is known (graph.microsoft.com:443)."));
        var opts  = new SharePointOptions { SiteId = "site-1", DriveId = "drive-1" };
        var sut   = new SharePointDataProvider(graph, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<Rag.NET.Models.RagError.TransportFailed>(result.Error);
        _ = Assert.IsType<HttpRequestException>(failure.Inner);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_TransportFailure_YieldsTransportFailedResult()
    {
        // The delta path has its own fetch helper (it also handles stale-token fallback),
        // so it needs its own coverage.
        const string deltaUrl = "https://graph.microsoft.com/v1.0/drives/drive-1/items/root/delta?token=tok1";
        var graph = MakeThrowingGraphClient(() => new HttpRequestException("connection reset by peer"));
        var opts  = new SharePointOptions { SiteId = "site-1", DriveId = "drive-1", DeltaToken = deltaUrl };
        var sut   = new SharePointDataProvider(graph, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        _ = Assert.IsType<Rag.NET.Models.RagError.TransportFailed>(result.Error);
    }

    [Fact]
    public async Task GetFilesAsync_CallerCancellation_Throws()
    {
        const string driveId = "drive-1";
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"/drives/{driveId}/items/root/children"] = """{ "value": [] }""",
        };
        var graph = MakeGraphClient(responses);
        var opts  = new SharePointOptions { SiteId = "site-1", DriveId = driveId };
        var sut   = new SharePointDataProvider(graph, opts);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Caller cancellation must not be reclassified as a transport failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    // -------------------------------------------------------------------------
    // Stale delta token → full-traversal fallback; every other ODataError → failure
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("resyncRequired", HttpStatusCode.Gone)]
    [InlineData("itemNotFound",   HttpStatusCode.NotFound)]
    public async Task GetFilesAsync_StaleDeltaToken_FallsBackToFullTraversal(
        string errorCode, HttpStatusCode status)
    {
        const string driveId  = "drive-1";
        const string deltaUrl = "https://graph.microsoft.com/v1.0/drives/drive-1/items/root/delta?token=stale";
        var childrenJson = """
            { "value": [ { "id": "file-1", "name": "readme.md", "file": {}, "eTag": "etag-1" } ] }
            """;

        var responses = new Dictionary<string, (HttpStatusCode, string)>(StringComparer.Ordinal)
        {
            [deltaUrl] = (status, $$"""{ "error": { "code": "{{errorCode}}", "message": "delta token no longer valid" } }"""),
            [$"/drives/{driveId}/items/root/children"] = (HttpStatusCode.OK, childrenJson),
        };
        var opts = new SharePointOptions { SiteId = "site-1", DriveId = driveId, DeltaToken = deltaUrl };
        var sut  = new SharePointDataProvider(MakeStatusGraphClient(responses), opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The stale token is not a failure — it means "start over with a full traversal",
        // and the full traversal's payload must actually be yielded.
        var entry = Assert.Single(results);
        Assert.True(entry.IsSuccess);
        Assert.Equal("readme.md", entry.Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_NonStaleODataError_FailsInsteadOfFallingBack()
    {
        const string driveId  = "drive-1";
        const string deltaUrl = "https://graph.microsoft.com/v1.0/drives/drive-1/items/root/delta?token=tok1";
        var childrenJson = """
            { "value": [ { "id": "file-1", "name": "readme.md", "file": {}, "eTag": "etag-1" } ] }
            """;

        var responses = new Dictionary<string, (HttpStatusCode, string)>(StringComparer.Ordinal)
        {
            [deltaUrl] = (HttpStatusCode.Forbidden,
                """{ "error": { "code": "accessDenied", "message": "insufficient privileges" } }"""),
            // Reachable, so a silent fallback would look like success — that is the bug guarded against.
            [$"/drives/{driveId}/items/root/children"] = (HttpStatusCode.OK, childrenJson),
        };
        var opts = new SharePointOptions { SiteId = "site-1", DriveId = driveId, DeltaToken = deltaUrl };
        var sut  = new SharePointDataProvider(MakeStatusGraphClient(responses), opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.True(result.IsFailure);
        var failure = Assert.IsType<Rag.NET.Models.RagError.HttpFailed>(result.Error);
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Contains("insufficient privileges", failure.Content, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Helper — builds a GraphServiceClient backed by a fake HTTP handler
    // -------------------------------------------------------------------------

    private static GraphServiceClient MakeGraphClient(Dictionary<string, string> responses)
        => MakeGraphClient(new FakeGraphHandler(responses));

    private static GraphServiceClient MakeThrowingGraphClient(Func<Exception> exceptionFactory)
        => MakeGraphClient(new ThrowingGraphHandler(exceptionFactory));

    private static GraphServiceClient MakeStatusGraphClient(
        Dictionary<string, (HttpStatusCode Status, string Body)> responses)
        => MakeGraphClient(new StatusGraphHandler(responses));

    private static GraphServiceClient MakeGraphClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        return new GraphServiceClient(httpClient);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>
/// Intercepts outbound Graph SDK HTTP calls and returns canned JSON payloads.
/// Matching is done against the URL path or the full URL.
/// </summary>
file sealed class FakeGraphHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        // Try full-URL match first (used for delta tokens that are complete URLs),
        // then try path-based matching for relative-ish keys like "/v1.0/drives/.../children".
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

/// <summary>
/// Fails every outbound Graph call before any HTTP response exists — the transport-failure
/// counterpart to <see cref="FakeGraphHandler"/>, which always produces a response.
/// A fresh exception instance is created per call so stack traces do not accumulate.
/// </summary>
file sealed class ThrowingGraphHandler(Func<Exception> exceptionFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => throw exceptionFactory();
}

/// <summary>
/// Like <see cref="FakeGraphHandler"/> but carries a status code per route, so a route can
/// return an OData error body and the Graph SDK will surface it as an <c>ODataError</c>.
/// Full-URL match first, then the longest matching substring key.
/// </summary>
file sealed class StatusGraphHandler(
    Dictionary<string, (HttpStatusCode Status, string Body)> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (responses.TryGetValue(url, out var exact))
            return Task.FromResult(ToResponse(exact));

        string? bestKey = null;
        int     bestLen = -1;
        foreach (var k in responses.Keys)
        {
            if (url.Contains(k, StringComparison.Ordinal) && k.Length > bestLen)
            {
                bestKey = k;
                bestLen = k.Length;
            }
        }

        return Task.FromResult(bestKey is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : ToResponse(responses[bestKey]));
    }

    private static HttpResponseMessage ToResponse((HttpStatusCode Status, string Body) canned)
        => new(canned.Status)
        {
            Content = new StringContent(canned.Body, System.Text.Encoding.UTF8, "application/json"),
        };
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
