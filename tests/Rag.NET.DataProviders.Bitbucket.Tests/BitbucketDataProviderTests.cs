using System.Net;
using System.Text;
using Rag.NET.DataProviders.Bitbucket;
using Refit;
using Xunit;

namespace Rag.NET.DataProviders.Bitbucket.Tests;

public sealed class BitbucketDataProviderTests
{
    private static BitbucketDataProvider MakeProvider(
        string sourceJson,
        BitbucketOptions? options = null,
        string urlKey = "/src/")
    {
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
            { [urlKey] = sourceJson });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.bitbucket.org/2.0/") };
        var api = RestService.For<IBitbucketApi>(http);
        return new BitbucketDataProvider(api, options ?? new BitbucketOptions
        {
            Workspace = "myteam",
            RepoSlug  = "myrepo"
        });
    }

    /// <summary>Overload that supports two-page pagination scenarios.</summary>
    private static BitbucketDataProvider MakeProvider(
        string page1Json,
        string page2Json,
        BitbucketOptions? options = null,
        string urlKey = "/src/")
    {
        var handler = new FakeHandler(
            new Dictionary<string, string>(StringComparer.Ordinal) { [urlKey] = page1Json },
            page2Responses: new Dictionary<string, string>(StringComparer.Ordinal) { [urlKey] = page2Json });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.bitbucket.org/2.0/") };
        var api = RestService.For<IBitbucketApi>(http);
        return new BitbucketDataProvider(api, options ?? new BitbucketOptions
        {
            Workspace = "myteam",
            RepoSlug  = "myrepo"
        });
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BitbucketDataProvider(null!, new BitbucketOptions
            {
                Workspace = "myteam",
                RepoSlug  = "myrepo"
            }));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsFiles()
    {
        const string json = """
            {
              "values": [
                { "path": "docs/readme.md", "type": "commit_file", "size": 42, "commit": { "hash": "abc123" } },
                { "path": "src/app.cs",     "type": "commit_file", "size": 99, "commit": { "hash": "def456" } },
                { "path": "src",            "type": "commit_directory", "size": null, "commit": null }
              ],
              "next": null
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("readme.md", results[0].Value.FileName);
        Assert.Equal("docs/readme.md", results[0].Value.Id);
        Assert.Equal("abc123", results[0].Value.ETag);
        Assert.Equal("app.cs", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_SkipsRemovedFiles()
    {
        const string json = """
            {
              "values": [
                { "status": "added",    "new": { "path": "new-file.md" } },
                { "status": "modified", "new": { "path": "changed.md" } },
                { "status": "removed",  "new": null },
                { "status": "renamed",  "new": { "path": "renamed.md" } }
              ],
              "next": null
            }
            """;
        var opts = new BitbucketOptions
        {
            Workspace             = "myteam",
            RepoSlug              = "myrepo",
            LastIngestedCommitHash = "aaa111"
        };
        var sut = MakeProvider(json, opts, urlKey: "/diffstat/");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal("new-file.md", results[0].Value.FileName);
        Assert.Equal("changed.md", results[1].Value.FileName);
        Assert.Equal("renamed.md", results[2].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatching()
    {
        const string json = """
            {
              "values": [
                { "path": "readme.md",  "type": "commit_file", "size": 10, "commit": { "hash": "aaa" } },
                { "path": "image.png",  "type": "commit_file", "size": 20, "commit": { "hash": "bbb" } },
                { "path": "code.cs",    "type": "commit_file", "size": 30, "commit": { "hash": "ccc" } }
              ],
              "next": null
            }
            """;
        var opts = new BitbucketOptions
        {
            Workspace  = "myteam",
            RepoSlug   = "myrepo",
            Extensions = [".md"]
        };
        var sut = MakeProvider(json, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("readme.md", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        const string json = """
            {
              "values": [
                { "path": "a.md", "type": "commit_file", "size": 1, "commit": { "hash": "h1" } },
                { "path": "b.md", "type": "commit_file", "size": 2, "commit": { "hash": "h2" } }
              ],
              "next": "https://api.bitbucket.org/2.0/repositories/t/r/src/main/?page=2"
            }
            """;
        var sut = MakeProvider(json);

        using var cts = new CancellationTokenSource();
        var count = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in sut.GetFilesAsync(cts.Token))
            {
                count++;
                if (count == 1)
                    cts.Cancel(); // cancel after first item
            }
        });

        Assert.Equal(1, count);
    }

    // -----------------------------------------------------------------------
    // New comprehensive tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullTraversal_SkipsDirectories()
    {
        const string json = """
            {
              "values": [
                { "path": "src",        "type": "commit_directory", "size": null, "commit": null },
                { "path": "src/app.cs", "type": "commit_file",     "size": 50,   "commit": { "hash": "aaa" } },
                { "path": "docs",       "type": "commit_directory", "size": null, "commit": null }
              ],
              "next": null
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("src/app.cs", results[0].Value.Id);
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_Pagination()
    {
        const string page1 = """
            {
              "values": [
                { "path": "file1.cs", "type": "commit_file", "size": 10, "commit": { "hash": "aaa" } }
              ],
              "next": "https://api.bitbucket.org/2.0/repositories/myteam/myrepo/src/main/?page=2"
            }
            """;
        const string page2 = """
            {
              "values": [
                { "path": "file2.cs", "type": "commit_file", "size": 20, "commit": { "hash": "bbb" } }
              ],
              "next": null
            }
            """;
        var sut = MakeProvider(page1, page2);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("file1.cs", results[0].Value.FileName);
        Assert.Equal("file2.cs", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_Pagination()
    {
        const string page1 = """
            {
              "values": [
                { "status": "added", "new": { "path": "a.cs" } }
              ],
              "next": "https://api.bitbucket.org/2.0/repositories/myteam/myrepo/diffstat/x..main?page=2"
            }
            """;
        const string page2 = """
            {
              "values": [
                { "status": "modified", "new": { "path": "b.cs" } }
              ],
              "next": null
            }
            """;
        var opts = new BitbucketOptions
        {
            Workspace             = "myteam",
            RepoSlug              = "myrepo",
            LastIngestedCommitHash = "x"
        };
        var sut = MakeProvider(page1, page2, opts, urlKey: "/diffstat/");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("a.cs", results[0].Value.FileName);
        Assert.Equal("b.cs", results[1].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_SkipsRenamedOldPath()
    {
        // "renamed" status: old path is gone, new path is the current location.
        // The provider should yield the NEW path, not the old.
        const string json = """
            {
              "values": [
                { "status": "renamed", "new": { "path": "new-location/file.cs" } }
              ],
              "next": null
            }
            """;
        var opts = new BitbucketOptions
        {
            Workspace             = "myteam",
            RepoSlug              = "myrepo",
            LastIngestedCommitHash = "prev"
        };
        var sut = MakeProvider(json, options: opts, urlKey: "/diffstat/");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("new-location/file.cs", results[0].Value.Id);
        Assert.Equal("file.cs", results[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_EmptySource_YieldsNothing()
    {
        const string json = """
            {
              "values": [],
              "next": null
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyDiffstat_YieldsNothing()
    {
        const string json = """
            {
              "values": [],
              "next": null
            }
            """;
        var opts = new BitbucketOptions
        {
            Workspace             = "myteam",
            RepoSlug              = "myrepo",
            LastIngestedCommitHash = "aaa"
        };
        var sut = MakeProvider(json, opts, urlKey: "/diffstat/");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFilesAsync_ContentReadable()
    {
        const string json = """
            {
              "values": [
                { "path": "hello.txt", "type": "commit_file", "size": 5, "commit": { "hash": "abc" } }
              ],
              "next": null
            }
            """;
        const string fileContent = "Hello, World!";
        var handler = new FakeHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/src/"] = json
        }, rawContent: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hello.txt"] = fileContent
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.bitbucket.org/2.0/") };
        var api = RestService.For<IBitbucketApi>(http);
        var sut = new BitbucketDataProvider(api, new BitbucketOptions
        {
            Workspace = "myteam",
            RepoSlug  = "myrepo"
        });

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        await using var stream = await results[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fileContent, content);
    }

    [Fact]
    public async Task GetFilesAsync_ETagFromSourceEntry()
    {
        const string json = """
            {
              "values": [
                { "path": "a.cs", "type": "commit_file", "size": 1, "commit": { "hash": "deadbeef" } }
              ],
              "next": null
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("deadbeef", results[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_FileNameIsLastPathSegment()
    {
        const string json = """
            {
              "values": [
                { "path": "src/deep/nested/main.cs", "type": "commit_file", "size": 1, "commit": { "hash": "h1" } }
              ],
              "next": null
            }
            """;
        var sut = MakeProvider(json);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("main.cs", results[0].Value.FileName);
        Assert.Equal("src/deep/nested/main.cs", results[0].Value.Id);
    }

    [Fact]
    public async Task GetFilesAsync_CustomRef_UsedInSourceUrl()
    {
        const string json = """
            {
              "values": [
                { "path": "a.cs", "type": "commit_file", "size": 1, "commit": { "hash": "h1" } }
              ],
              "next": null
            }
            """;
        var opts = new BitbucketOptions
        {
            Workspace = "myteam",
            RepoSlug  = "myrepo",
            Ref       = "develop"
        };
        // Use "/develop/" as urlKey — the source URL will contain the ref in the path
        var sut = MakeProvider(json, opts, urlKey: "/develop/");

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.Equal("a.cs", results[0].Value.FileName);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handlers
// ---------------------------------------------------------------------------

/// <summary>
/// Returns canned JSON responses keyed by URL substring, so tests never hit the network.
/// Supports pagination via the <c>paginatedResponses</c> constructor overload and
/// raw file content via the <c>rawContent</c> parameter.
/// </summary>
file sealed class FakeHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;
    private readonly Dictionary<string, string>? _rawContent;
    private readonly Dictionary<string, string>? _page2Responses;

    public FakeHandler(
        Dictionary<string, string> responses,
        Dictionary<string, string>? rawContent = null,
        Dictionary<string, string>? page2Responses = null)
    {
        _responses = responses;
        _rawContent = rawContent;
        _page2Responses = page2Responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        // Check raw file content first (for content readability tests)
        if (_rawContent is not null)
        {
            var rawKey = _rawContent.Keys.FirstOrDefault(k => url.Contains(k,
                StringComparison.Ordinal));
            if (rawKey is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_rawContent[rawKey], Encoding.UTF8,
                        "application/octet-stream")
                });
        }

        // Check page 2 responses when URL contains page=2
        if (_page2Responses is not null && url.Contains("page=2", StringComparison.Ordinal))
        {
            var p2Key = _page2Responses.Keys.FirstOrDefault(k => url.Contains(k,
                StringComparison.Ordinal));
            if (p2Key is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_page2Responses[p2Key], Encoding.UTF8,
                        "application/json")
                });
        }

        var key = _responses.Keys.FirstOrDefault(k => url.Contains(k,
            StringComparison.Ordinal));
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses[key], Encoding.UTF8, "application/json")
        });
    }
}
