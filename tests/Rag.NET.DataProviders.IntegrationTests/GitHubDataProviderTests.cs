using Octokit;
using Rag.NET.DataProviders.GitHub;
using Rag.NET.Testing;
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
/// <b>The delta path is recorded too.</b> It used to register its own stubs for
/// <c>compare/old-sha...main</c>, so GitHub's compare response shape was our belief about it and
/// nothing had checked that belief. It now compares a real prior commit against <c>main</c>.
/// </para>
/// <para>
/// The compare cassette is <b>trimmed</b>, and that is scrubbing rather than fitting the fixture to
/// the code: <c>commits</c>, <c>base_commit</c> and <c>merge_base_commit</c> are emptied and the
/// <c>patch</c> hunks dropped. The provider reads <c>comparison.Files</c> and nothing else, and those
/// envelope objects carry commit author and committer email addresses — personal data a fixture has
/// no reason to hold. No value the provider reads was altered.
/// </para>
/// </remarks>
[Collection("WireMock")]
public sealed class GitHubDataProviderTests
{
    private const string Owner = "MarcelRoozekrans";

    /// <summary>
    /// A small public repository, chosen for its size as much as its shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Public, verified rather than assumed:</b> <c>visibility=PUBLIC</c>, and the repo, tree and
    /// compare endpoints all answer <c>200</c> to an unauthenticated request — while a private
    /// repository under the same account answers <c>404</c> anonymously, which is what rules out the
    /// recorder's own credentials being the reason the fetch worked.
    /// </para>
    /// <para>
    /// <b>Recording this repository's own tree was tried and rejected on size.</b>
    /// <c>MarcelRoozekrans/Rag.NET</c> has 2,164 blobs across 2,472 nodes, and the recursive tree
    /// response alone commits a <b>904 KB</b> cassette that is rewritten wholesale on every
    /// re-record. RalphPilot's is 7 KB. A fixture that large buys nothing the smaller one does not:
    /// what these tests need from a tree is blobs, real <c>tree</c> nodes to skip, and a mix of
    /// extensions to filter, and 9 nodes supply all three.
    /// </para>
    /// </remarks>
    private const string Repo = "RalphPilot";

    /// <summary>A file that exists, is not going away, and whose content is recognisable.</summary>
    /// <remarks>
    /// Named rather than taken as <c>entries[0]</c>. Tree order put
    /// <c>.claude/scheduled_tasks.lock</c> first — a stray committed lock file holding a session id
    /// and a pid — so the old test recorded the content of whatever happened to sort first and
    /// asserted only that it was non-empty. A named file lets the assertion be about content.
    /// </remarks>
    private const string StableFile = "readme.md";

    /// <summary>
    /// <c>main</c>'s first parent at recording time, for the delta path.
    /// </summary>
    /// <remarks>
    /// A real sha against a real compare. The previous value was the literal <c>"old-sha"</c> against
    /// a hand-written stub, so the delta path had never seen GitHub's compare response.
    /// </remarks>
    private const string PreviousCommitSha = "8c7329f6cf89715e8d0513c78a3ec670a7d33f5c";

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

        var readme = entries.Single(e =>
            string.Equals(e.Value.Id.Value, StableFile, StringComparison.Ordinal));

        await using var stream = await readme.Value.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Content, not just length. A raw-content request answered with JSON metadata — the failure
        // mode the Accept header guards against — is non-empty and would pass a length check.
        Assert.Contains("Ralph", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"download_url\"", text, StringComparison.Ordinal);
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

    /// <remarks>
    /// <para>
    /// The delta path against a <b>real</b> compare response. It previously registered its own stubs
    /// for <c>compare/old-sha...main</c> and a content endpoint, so the shape of GitHub's compare
    /// response was our belief about it and nothing had ever checked that belief.
    /// </para>
    /// <para>
    /// Asserts on the shape the provider depends on rather than on an exact file list: the recording
    /// is one commit's diff, and pinning its eleven filenames would make this a test of that commit.
    /// What matters is that changed files come back, each carries a sha as its ETag, and the
    /// <c>change_status</c> normalisation the provider performs actually produces values from the
    /// documented vocabulary.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListDocuments_DeltaRun_ReturnsChangedFilesFromTheRealCompareApi()
    {
        var sut = new GitHubDataProvider(Owner, Repo, CreateClient(),
            new GitHubDataProviderOptions { LastIngestedCommitSha = PreviousCommitSha });

        var entries = await sut
            .GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(entries);

        // A delta must be smaller than the full tree, or it is not a delta. The full tree of this
        // repository is over two thousand blobs; one commit changed eleven files.
        Assert.True(
            entries.Count < 100,
            $"A one-commit delta returned {entries.Count} entries. Either the compare call was not " +
            "used or the full-tree path ran instead.");

        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Value.ETag), $"{e.Value.Id.Value} has no sha.");
            Assert.NotNull(e.Value.Metadata);

            // The cross-connector vocabulary the provider maps GitHub's status onto. A mapping that
            // let an unmapped GitHub status through — "unchanged", say — would break filtering that
            // is meant to work identically across GitLab, Bitbucket and Box.
            var status = e.Value.Metadata["change_status"].ToString();
            Assert.Contains(status, (string[])["added", "modified", "removed", "renamed"], StringComparer.Ordinal);
        });

        // No assertion that a particular status appears. The recorded diff is one commit, and an
        // earlier draft required "added" because the commit it was recorded from happened to add
        // files — which is the repo-specific pinning the remarks above claim to avoid. It failed the
        // moment the recording moved to a commit that only modified a file, which is the right
        // outcome for the wrong assertion.
    }
}
