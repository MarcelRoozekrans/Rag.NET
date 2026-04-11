using NSubstitute;
using Octokit;
using Rag.NET.DataProviders.GitHub;
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
}
