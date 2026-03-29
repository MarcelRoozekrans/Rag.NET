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
        Assert.Equal("readme.md", results[0].FileName);
        Assert.Equal("docs/readme.md", results[0].Id);
        Assert.Equal("abc123", results[0].ETag);
        Assert.Equal("app.cs", results[1].FileName);
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
        Assert.Equal("new-file.md", results[0].FileName);
        Assert.Equal("changed.md", results[1].FileName);
        Assert.Equal("renamed.md", results[2].FileName);
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

        Assert.Single(results);
        Assert.Equal("readme.md", results[0].FileName);
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
}

// ---------------------------------------------------------------------------
// Test infrastructure — fake HTTP handlers
// ---------------------------------------------------------------------------

/// <summary>
/// Returns canned JSON responses keyed by URL substring, so tests never hit the network.
/// </summary>
file sealed class FakeHandler(Dictionary<string, string> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var key = responses.Keys.FirstOrDefault(k => url.Contains(k,
            StringComparison.Ordinal));
        if (key is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses[key], Encoding.UTF8, "application/json")
        });
    }
}
