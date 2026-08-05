using NGitLab;
using NGitLab.Models;
using NSubstitute;
using Rag.NET.DataProviders.GitLab;
using Rag.NET.DataProviders.Testing;
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
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "docs/readme.md", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "src/main.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// Phase 4.10 Task 5: neither the tree listing (<c>Tree</c>) nor the compare-API diff
    /// entries carry a commit date — a blob is just a path/mode/sha/type. This is a deliberate
    /// "checked and there is none", not an oversight: pulling the date would need a separate
    /// commit lookup per file, out of scope for this task.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_FullTraversal_NoTimestampAvailable_LeavesBothUnset()
    {
        var items = new[] { MakeTreeItem("docs/readme.md", "aaa111") };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Null(entries[0].Value.CreatedAt);
        Assert.Null(entries[0].Value.UpdatedAt);
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

        _ = Assert.Single(entries);
        Assert.Equal("kept.md", entries[0].Value.Id);
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
            e => string.Equals(e.Value.Id, "build.yaml", StringComparison.Ordinal));
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

    [Fact]
    public async Task GetFilesAsync_FullTraversal_SkipsNonBlobs()
    {
        var items = new[]
        {
            MakeTreeItem("src/main.cs", "aaa111", ObjectType.blob),
            MakeTreeItem("src/utils", "bbb222", ObjectType.tree),
            MakeTreeItem("vendor/lib", "ccc333", ObjectType.commit), // submodule
        };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("src/main.cs", entries[0].Value.Id);
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_FileNameIsLastSegment()
    {
        var items = new[]
        {
            MakeTreeItem("src/utils/helper.cs", "aaa111"),
        };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal("helper.cs", entries[0].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_FullTraversal_ETagIsBlobSha()
    {
        var sha = "abcdef1234567890abcdef1234567890abcdef12";
        var items = new[]
        {
            MakeTreeItem("readme.md", sha),
        };
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        Assert.Equal(sha, entries[0].Value.ETag, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFilesAsync_DeltaTraversal_SkipsDeletedAndRenamedOldPath()
    {
        var comparison = new CompareResults
        {
            Diff =
            [
                new Diff { NewPath = "new-name.cs", OldPath = "old-name.cs", IsDeletedFile = false, IsRenamedFile = true },
                new Diff { NewPath = "removed.cs", OldPath = "removed.cs", IsDeletedFile = true },
                new Diff { NewPath = "modified.cs", OldPath = "modified.cs", IsDeletedFile = false },
            ],
        };
        var (client, _) = MakeClient(compareResults: comparison);
        var sut = new GitLabDataProvider(client,
            MakeOptions(lastIngestedCommitSha: "old-sha"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Deleted file is skipped; renamed file yields the new path
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "new-name.cs", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.Value.Id, "modified.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => string.Equals(e.Value.Id, "removed.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetFilesAsync_EmptyTree_YieldsNothing()
    {
        var (client, _) = MakeClient(treeItems: []);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyDelta_YieldsNothing()
    {
        var comparison = new CompareResults
        {
            Diff = [],
        };
        var (client, _) = MakeClient(compareResults: comparison);
        var sut = new GitLabDataProvider(client,
            MakeOptions(lastIngestedCommitSha: "old-sha"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetFilesAsync_ContentReadable()
    {
        var expectedBytes = "hello world"u8.ToArray();
        var items = new[]
        {
            MakeTreeItem("file.txt", "aaa111"),
        };
        var (client, repo) = MakeClient(treeItems: items);

        // Override the default GetRawAsync stub to write actual content
        repo.Files.GetRawAsync(
            Arg.Is("file.txt"),
            Arg.Any<Func<Stream, Task>>(),
            Arg.Any<GetRawFileRequest>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var callback = callInfo.ArgAt<Func<Stream, Task>>(1);
                return callback(new MemoryStream(expectedBytes));
            });

        var sut = new GitLabDataProvider(client, MakeOptions());
        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(entries);
        await using var stream = await entries[0].Value.OpenContentAsync(TestContext.Current.CancellationToken);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal(expectedBytes, ms.ToArray());
    }

    [Fact]
    public async Task GetFilesAsync_CustomRef_UsesSpecifiedBranch()
    {
        var items = new[]
        {
            MakeTreeItem("file.md", "aaa111"),
        };
        var (client, repo) = MakeClient(treeItems: items);
        var opts = new GitLabOptions
        {
            BaseUrl = "https://gitlab.com",
            ProjectIdOrPath = Project,
            Ref = "develop",
        };
        var sut = new GitLabDataProvider(client, opts);

        _ = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        repo.Received(1).GetTreeAsync(Arg.Is<RepositoryGetTreeOptions>(o =>
            o!.Ref == "develop" && o.Recursive == true));
    }

    [Fact]
    public async Task GetFilesAsync_MultipleItems_YieldsAll()
    {
        var items = Enumerable.Range(0, 25)
            .Select(i => MakeTreeItem($"dir/file{i}.cs", $"sha{i}"))
            .ToArray();
        var (client, _) = MakeClient(treeItems: items);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(25, entries.Count);
        for (var i = 0; i < 25; i++)
        {
            Assert.Contains(entries, e => string.Equals(e.Value.Id, $"dir/file{i}.cs", StringComparison.Ordinal));
        }
    }

    // -----------------------------------------------------------------------
    // Metadata — the exact keys and values this connector emits
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullTraversal_EmitsPathProjectAndRef()
    {
        var (client, _) = MakeClient(treeItems: [MakeTreeItem("docs/readme.md", "aaa111")]);
        var opts = new GitLabOptions
        {
            BaseUrl = "https://gitlab.com",
            ProjectIdOrPath = Project,
            Ref = "develop",
        };
        var sut = new GitLabDataProvider(client, opts);

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("docs/readme.md", metadata["path"]);
        Assert.Equal("org/repo",       metadata["project"]);
        Assert.Equal("develop",        metadata["ref"]);
        // A full tree traversal has no notion of change — the key must be absent, not empty.
        Assert.False(metadata.ContainsKey("change_status"));
        Assert.Equal(3, metadata.Count);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Theory]
    [InlineData(true,  false, "added")]
    [InlineData(false, true,  "renamed")]
    [InlineData(false, false, "modified")]
    public async Task GetFilesAsync_DeltaTraversal_NormalisesChangeStatus(
        bool isNewFile, bool isRenamedFile, string expected)
    {
        var comparison = new CompareResults
        {
            Diff =
            [
                new Diff
                {
                    NewPath = "file.md",
                    OldPath = "file.md",
                    IsNewFile = isNewFile,
                    IsRenamedFile = isRenamedFile,
                    IsDeletedFile = false,
                },
            ],
        };
        var (client, _) = MakeClient(compareResults: comparison);
        var sut = new GitLabDataProvider(client, MakeOptions(lastIngestedCommitSha: "old-sha"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var metadata = Assert.Single(entries).Value.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal(expected,   metadata["change_status"]);
        Assert.Equal("file.md",  metadata["path"]);
        Assert.Equal("org/repo", metadata["project"]);
        Assert.Equal("main",     metadata["ref"]);
        Assert.Equal(4, metadata.Count);
        MetadataContract.AssertValid(entries[0].Value);
    }

    [Fact]
    public async Task GetFilesAsync_AllEntriesSatisfyMetadataContract()
    {
        var (client, _) = MakeClient(treeItems:
        [
            MakeTreeItem("docs/readme.md", "aaa111"),
            MakeTreeItem("src/main.cs", "bbb222"),
        ]);
        var sut = new GitLabDataProvider(client, MakeOptions());

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        MetadataContract.AssertAll(entries.Select(e => e.Value));
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
