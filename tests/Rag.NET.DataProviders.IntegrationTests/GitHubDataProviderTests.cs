using Octokit;
using Rag.NET.DataProviders.GitHub;
using Rag.NET.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Rag.NET.DataProviders.IntegrationTests;

/// <summary>
/// Replays cassettes recorded from the real GitHub API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recorded 2026-08-17</b> from <c>MarcelRoozekrans/RalphPilot</c> at commit <c>67b12987</c> —
/// 7 blobs, the real tree nodes <c>.ralph</c> and <c>.ralph/specs</c>, and a mix of <c>.md</c> and
/// <c>.ps1</c>. That mix is why the repo was chosen: the extension filter test asserts something
/// only when the tree contains files it must exclude, and <c>SkipsTreeNodes</c> previously relied on
/// a hand-written <c>src/</c> node that no real response had produced.
/// </para>
/// <para>
/// <b>Recorded unauthenticated</b>, deliberately. The repository is public, the API allows it inside
/// the 60-requests-per-hour anonymous limit, and the result is that no credential appears in the
/// fixtures at all — nothing to scrub, and nothing to leak.
/// </para>
/// <para>
/// <b>The recording harness did not work before this.</b> Two independent defects, both invisible at
/// the moment they struck because record mode proxies to the real service and therefore passes. See
/// <see cref="Rag.NET.Testing.WireMockServerFixture"/>: recordings were written to a directory replay
/// never read, and every recorded mapping matched on <c>Host: localhost:{ephemeral port}</c>, which
/// cannot match twice. This suite is the first cassette in the repository actually recorded and
/// replayed.
/// </para>
/// <para>
/// <b>Still hand-written:</b> the delta path. <c>ListDocuments_DeltaRun_ReturnsOnlyChangedFiles</c>
/// registers its own stubs for the compare endpoint, so the shape of GitHub's compare response is
/// still our belief about it rather than an observation. Recording it needs a real prior commit sha
/// to compare against, which is a further piece of work rather than a difficulty — noted here so the
/// gap is not mistaken for coverage.
/// </para>
/// </remarks>
[Collection("WireMock")]
public sealed class GitHubDataProviderTests
{
    private const string Owner = "MarcelRoozekrans";
    private const string Repo = "RalphPilot";

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
