using NGitLab;
using NGitLab.Models;
using NSubstitute;
using Rag.NET.DataProviders.GitLab;
using Xunit;

namespace Rag.NET.DataProviders.GitLab.Tests;

public sealed class GitLabDataProviderTests
{
    private const string Project = "org/repo";

    private static GitLabOptions MakeOptions(
        string? lastIngestedCommitSha = null,
        IReadOnlyList<string>? extensions = null)
    {
        var opts = new GitLabOptions
        {
            BaseUrl = "https://gitlab.com",
            ProjectIdOrPath = Project,
        };

        // Use reflection to set init-only properties when needed for tests.
        if (lastIngestedCommitSha is not null)
        {
            return new GitLabOptions
            {
                BaseUrl = "https://gitlab.com",
                ProjectIdOrPath = Project,
                LastIngestedCommitSha = lastIngestedCommitSha,
                Extensions = extensions ?? ["*"],
            };
        }

        if (extensions is not null)
        {
            return new GitLabOptions
            {
                BaseUrl = "https://gitlab.com",
                ProjectIdOrPath = Project,
                Extensions = extensions,
            };
        }

        return opts;
    }

    private static (IGitLabClient client, IRepositoryClient repo) MakeClient(
        Tree[]? treeItems = null,
        CompareResults? compareResults = null)
    {
        var client = Substitute.For<IGitLabClient>();
        var repo = Substitute.For<IRepositoryClient>();
        var files = Substitute.For<IFilesClient>();
        repo.Files.Returns(files);

        client.GetRepository(Arg.Any<ProjectId>()).Returns(repo);

        if (treeItems is not null)
        {
            var collectionResponse = Substitute.For<GitLabCollectionResponse<Tree>>();
            collectionResponse.GetAsyncEnumerator(Arg.Any<CancellationToken>())
                .Returns(_ => new ArrayAsyncEnumerator<Tree>(treeItems));
            repo.GetTreeAsync(Arg.Any<RepositoryGetTreeOptions>()).Returns(collectionResponse);
        }

        if (compareResults is not null)
        {
            repo.CompareAsync(Arg.Any<CompareQuery>(), Arg.Any<CancellationToken>())
                .Returns(compareResults);
        }

        // Stub GetRawAsync to write empty content
        files.GetRawAsync(
            Arg.Any<string>(),
            Arg.Any<Func<Stream, Task>>(),
            Arg.Any<GetRawFileRequest>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return (client, repo);
    }

    private static Tree MakeTreeItem(string path, string sha, ObjectType type = ObjectType.blob)
        => new() { Path = path, Name = Path.GetFileName(path), Id = new Sha1(sha.PadRight(40, '0')), Type = type, Mode = "100644" };

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new GitLabDataProvider(null!, MakeOptions()));
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_YieldsBlobs()
    {
        var items = new[]
        {
            MakeTreeItem("docs/readme.md", "aaa111"),
            MakeTreeItem("src/main.cs", "bbb222"),
        };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Id, "docs/readme.md", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Id, "src/main.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_SkipsDeletedFiles()
    {
        var comparison = new CompareResults
        {
            Diff =
            [
                new Diff { NewPath = "kept.md", OldPath = "kept.md", IsDeletedFile = false },
                new Diff { NewPath = "deleted.md", OldPath = "deleted.md", IsDeletedFile = true },
            ],
        };
        var (client, _) = MakeClient(compareResults: comparison);
        var sut = new GitLabDataProvider(client,
            MakeOptions(lastIngestedCommitSha: "old-sha"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal("kept.md", entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatching()
    {
        var items = new[]
        {
            MakeTreeItem("readme.md", "aaa111"),
            MakeTreeItem("build.yaml", "bbb222"),
            MakeTreeItem("src/main.cs", "ccc333"),
        };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client,
            MakeOptions(extensions: [".md", ".cs"]));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries,
            e => string.Equals(e.Id, "build.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_CancellationRequested_Throws()
    {
        var items = new[]
        {
            MakeTreeItem("file1.md", "aaa111"),
            MakeTreeItem("file2.md", "bbb222"),
        };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.GetFilesAsync(cts.Token).ToListAsync(cts.Token));
    }

    /// <summary>Simple async enumerator over an array for test mocking.</summary>
    private sealed class ArrayAsyncEnumerator<T>(T[] items) : IAsyncEnumerator<T>
    {
        private int _index = -1;

        public T Current => items[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            _index++;
            return ValueTask.FromResult(_index < items.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
