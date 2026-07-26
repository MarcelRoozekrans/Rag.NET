using NSubstitute;
using Octokit;
using Rag.NET.DataProviders.GitHub;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.GitHub.Tests;

public sealed class GitHubDataProviderTests
{
    private const string Owner = "org";
    private const string Repo = "repo";

    private static IGitHubClient MakeClient(
        TreeResponse tree,
        CompareResult? compareResult = null)
    {
        var client = Substitute.For<IGitHubClient>();

        client.Git.Tree.GetRecursive(Owner, Repo, Arg.Any<string>())
            .Returns(tree);

        client.Repository.Content
            .GetRawContent(Owner, Repo, Arg.Any<string>())
            .Returns(Array.Empty<byte>());

        if (compareResult is not null)
        {
            client.Repository.Commit
                .Compare(Owner, Repo, Arg.Any<string>(), Arg.Any<string>())
                .Returns(compareResult);
        }

        return client;
    }

    private static TreeResponse MakeTree(params (string path, string sha)[] items)
    {
        var treeItems = items
            .Select(i => new TreeItem(i.path, "100644", TreeType.Blob, 100, i.sha,
                $"https://api.github.com/repos/{Owner}/{Repo}/git/blobs/{i.sha}"))
            .ToList();
        return new TreeResponse("abc123", "https://api.github.com", treeItems, truncated: false);
    }

    [Fact]
    public async Task GetFilesAsync_FullTree_ReturnsAllBlobs()
    {
        var tree = MakeTree(("docs/readme.md", "sha-1"), ("src/main.cs", "sha-2"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "docs/readme.md", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "src/main.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FullTree_BlobShaBecomesETag()
    {
        var tree = MakeTree(("docs/readme.md", "sha-abc"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sha-abc", entries[0].Value.ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var tree = MakeTree(("readme.md", "sha-1"), ("build.yaml", "sha-2"), ("src/main.cs", "sha-3"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree),
            new GitHubDataProviderOptions { Extensions = [".md", ".cs"] });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => string.Equals(e.Value.Id, "build.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_PredicateFilter_ExcludesMatchedPaths()
    {
        var tree = MakeTree(("docs/guide.md", "sha-1"), ("docs/plans/internal.md", "sha-2"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree),
            new GitHubDataProviderOptions { Filter = path => !path.StartsWith("docs/plans/", StringComparison.Ordinal) });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("docs/guide.md", entries[0].Value.Id);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_OnlyReturnsChangedFiles()
    {
        var compareResult = new CompareResult(
            url: "", htmlUrl: "", permalinkUrl: "", diffUrl: "", patchUrl: "",
            baseCommit: null!, mergeBaseCommit: null!,
            status: "ahead", aheadBy: 2, behindBy: 0, totalCommits: 2,
            commits: [],
            files:
            [
                new GitHubCommitFile("changed.md", 0, 0, 0, "modified", "", "", "", "new-sha", "", ""),
            ]);

        var tree = MakeTree(); // full tree not called in delta mode
        var client = MakeClient(tree, compareResult);
        var sut = new GitHubDataProvider(Owner, Repo, client,
            new GitHubDataProviderOptions { LastIngestedCommitSha = "old-sha" });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("changed.md", entries[0].Value.Id);
        // Verify full tree was NOT called
        await client.Git.Tree.DidNotReceive().GetRecursive(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_SkipsRemovedFiles()
    {
        var compareResult = new CompareResult(
            url: "", htmlUrl: "", permalinkUrl: "", diffUrl: "", patchUrl: "",
            baseCommit: null!, mergeBaseCommit: null!,
            status: "ahead", aheadBy: 2, behindBy: 0, totalCommits: 2,
            commits: [],
            files:
            [
                new GitHubCommitFile("kept.md", 0, 0, 0, "modified", "", "", "", "sha-kept", "", ""),
                new GitHubCommitFile("deleted.md", 0, 0, 0, "removed", "", "", "", "sha-del", "", ""),
            ]);

        var tree = MakeTree();
        var client = MakeClient(tree, compareResult);
        var sut = new GitHubDataProvider(Owner, Repo, client,
            new GitHubDataProviderOptions { LastIngestedCommitSha = "old-sha" });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("kept.md", entries[0].Value.Id);
        Assert.DoesNotContain(entries, e => string.Equals(e.Value.Id, "deleted.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_FullTree_ExcludesNonBlobItems()
    {
        // Mix one directory (TreeType.Tree) with one file (TreeType.Blob)
        var treeItems = new List<TreeItem>
        {
            new TreeItem("src/",        "040000", TreeType.Tree, 0,   "tree-sha",
                $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/tree-sha"),
            new TreeItem("src/main.cs", "100644", TreeType.Blob, 500, "blob-sha",
                $"https://api.github.com/repos/{Owner}/{Repo}/git/blobs/blob-sha"),
        };
        var tree = new TreeResponse("abc123", "https://api.github.com", treeItems, truncated: false);
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("src/main.cs", entries[0].Value.Id);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var compareResult = new CompareResult(
            url: "", htmlUrl: "", permalinkUrl: "", diffUrl: "", patchUrl: "",
            baseCommit: null!, mergeBaseCommit: null!,
            status: "ahead", aheadBy: 2, behindBy: 0, totalCommits: 2,
            commits: [],
            files:
            [
                new GitHubCommitFile("changed.md",   0, 0, 0, "modified", "", "", "", "sha-md",   "", ""),
                new GitHubCommitFile("changed.yaml", 0, 0, 0, "modified", "", "", "", "sha-yaml", "", ""),
            ]);

        var tree = MakeTree(); // full tree not used in delta mode
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree, compareResult),
            new GitHubDataProviderOptions
            {
                LastIngestedCommitSha = "old-sha",
                Extensions = [".md"],
            });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("changed.md", entries[0].Value.Id);
    }

    // -----------------------------------------------------------------------
    // Metadata — the exact keys and values this connector emits
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullTree_EmitsPathRepoAndRef()
    {
        var tree = MakeTree(("docs/readme.md", "sha-1"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree),
            new GitHubDataProviderOptions { Branch = "develop" });

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("docs/readme.md", metadata["path"]);
        Assert.Equal("org/repo",       metadata["repo"]);
        Assert.Equal("develop",        metadata["ref"]);
        // A full tree traversal has no notion of change — the key must be absent, not empty.
        Assert.False(metadata.ContainsKey("change_status"));
        Assert.Equal(3, metadata.Count);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Theory]
    [InlineData("added",     "added")]
    [InlineData("copied",    "added")]
    [InlineData("modified",  "modified")]
    [InlineData("changed",   "modified")]
    [InlineData("renamed",   "renamed")]
    public async Task GetFilesAsync_DeltaRun_NormalisesChangeStatus(
        string githubStatus, string expected)
    {
        var sut = MakeDeltaProvider(("file.md", githubStatus));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal(expected,  metadata["change_status"]);
        Assert.Equal("file.md", metadata["path"]);
        Assert.Equal("org/repo", metadata["repo"]);
        Assert.Equal("main",     metadata["ref"]);
        Assert.Equal(4, metadata.Count);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_UnmappedStatus_OmitsChangeStatus()
    {
        // "unchanged" asserts no change at all; forwarding it would put a vendor-only value in a
        // tag the other connectors cannot produce, so the key is omitted instead.
        var sut = MakeDeltaProvider(("file.md", "unchanged"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.False(metadata.ContainsKey("change_status"));
        Assert.Equal(3, metadata.Count);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Fact]
    public async Task GetFilesAsync_AllEntriesSatisfyMetadataContract()
    {
        var tree = MakeTree(("docs/readme.md", "sha-1"), ("src/main.cs", "sha-2"));
        var sut = new GitHubDataProvider(Owner, Repo, MakeClient(tree));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        MetadataContract.AssertAll(entries.Select(e => e.Value));
    }

    private static GitHubDataProvider MakeDeltaProvider(params (string path, string status)[] files)
    {
        var compareResult = new CompareResult(
            url: "", htmlUrl: "", permalinkUrl: "", diffUrl: "", patchUrl: "",
            baseCommit: null!, mergeBaseCommit: null!,
            status: "ahead", aheadBy: 1, behindBy: 0, totalCommits: 1,
            commits: [],
            files: [.. files.Select(f =>
                new GitHubCommitFile(f.path, 0, 0, 0, f.status, "", "", "", "sha", "", ""))]);

        return new GitHubDataProvider(Owner, Repo, MakeClient(MakeTree(), compareResult),
            new GitHubDataProviderOptions { LastIngestedCommitSha = "old-sha" });
    }
}
