using Octokit;
using Rag.NET.DataProviders.GitHub;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

[Collection("WireMock")]
public sealed class GitHubDataProviderTests
{
    private const string Owner = "test-owner";
    private const string Repo = "test-repo";

    private readonly WireMockServerFixture _fixture;

    public GitHubDataProviderTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("GitHub", "https://api.github.com");
    }

    private IGitHubClient CreateClient()
    {
        // Point Octokit at the WireMock server instead of api.github.com.
        var baseUri = new Uri(_fixture.BaseUrl + "/");
        var connection = new Connection(
            new ProductHeaderValue("ragnet-integration-test"),
            baseUri);
        return new GitHubClient(connection);
    }

    [Fact]
    public async Task ListDocuments_FullTree_ReturnsAllBlobs()
    {
        var sut = new GitHubDataProvider(Owner, Repo, CreateClient());

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.NotEmpty(e.Value.Id.Value);
            Assert.NotEmpty(e.Value.FileName);
        });
    }

    [Fact]
    public async Task ListDocuments_FullTree_EachEntryHasETag()
    {
        var sut = new GitHubDataProvider(Owner, Repo, CreateClient());

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(entries, e => Assert.NotEmpty(e.Value.ETag!));
    }

    [Fact]
    public async Task ListDocuments_FullTree_SkipsTreeNodes()
    {
        var sut = new GitHubDataProvider(Owner, Repo, CreateClient());

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The cassette includes a tree node (src/) — it must not appear in results.
        Assert.DoesNotContain(entries, e => e.Value.Id.Value.EndsWith('/'));
    }

    [Fact]
    public async Task ListDocuments_FullTree_OpenContent_ReturnsNonEmptyStream()
    {
        var sut = new GitHubDataProvider(Owner, Repo, CreateClient());

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Open the first file and verify we get a non-empty stream.
        var first = entries[0];
        await using var stream = await first.Value.OpenContentAsync(TestContext.Current.CancellationToken);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task ListDocuments_ExtensionFilter_OnlyReturnsMatchingFiles()
    {
        var sut = new GitHubDataProvider(Owner, Repo, CreateClient(),
            new GitHubDataProviderOptions { Extensions = [".md"] });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
            Assert.EndsWith(".md", e.Value.FileName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListDocuments_DeltaRun_ReturnsOnlyChangedFiles()
    {
        // Register programmatic stubs for the compare and changed-file-content endpoints.
        _fixture.Server
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/compare/old-sha...main")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json; charset=utf-8")
                .WithBody("""
                    {
                      "url": "",
                      "html_url": "",
                      "permalink_url": "",
                      "diff_url": "",
                      "patch_url": "",
                      "base_commit": null,
                      "merge_base_commit": null,
                      "status": "ahead",
                      "ahead_by": 1,
                      "behind_by": 0,
                      "total_commits": 1,
                      "commits": [],
                      "files": [
                        {
                          "sha": "delta-sha-001",
                          "filename": "CHANGELOG.md",
                          "status": "modified",
                          "additions": 5,
                          "deletions": 0,
                          "changes": 5,
                          "blob_url": "",
                          "raw_url": "",
                          "contents_url": "",
                          "patch": ""
                        }
                      ]
                    }
                    """));

        _fixture.Server
            .Given(Request.Create()
                .WithPath($"/repos/{Owner}/{Repo}/contents/CHANGELOG.md")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/octet-stream")
                .WithBody("## Changelog\n\n### v1.1.0\n- Fixed bug\n"));

        var sut = new GitHubDataProvider(Owner, Repo, CreateClient(),
            new GitHubDataProviderOptions { LastIngestedCommitSha = "old-sha" });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("CHANGELOG.md", entries[0].Value.Id);
        Assert.Equal("delta-sha-001", entries[0].Value.ETag);
    }
}
