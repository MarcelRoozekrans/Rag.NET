using System.Runtime.CompilerServices;
using Rag.NET.DataProviders;
using Xunit;

namespace Rag.NET.DataProviders.Tests;

public sealed class FileContentProviderBaseTests
{
    private sealed class StubProvider(
        CloudStorageOptions options,
        params FileHandle[] handles) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<FileHandle> GetFileHandlesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var h in handles)
                yield return h;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static FileHandle Handle(string id, string fileName, string? etag = null)
        => new(id, fileName, etag, _ => Task.FromResult<Stream>(new MemoryStream()));

    private sealed class TestOptions : CloudStorageOptions { }

    [Fact]
    public async Task GetFilesAsync_NoFilter_YieldsAllHandles()
    {
        var sut = new StubProvider(new TestOptions(),
            Handle("a/file.md", "file.md"),
            Handle("b/file.cs", "file.cs"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_ExcludesNonMatchingFiles()
    {
        var options = new TestOptions { Extensions = [".md"] };
        var sut = new StubProvider(options,
            Handle("readme.md", "readme.md"),
            Handle("build.yaml", "build.yaml"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("readme.md", results[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_WildcardExtension_YieldsAllFiles()
    {
        var options = new TestOptions { Extensions = ["*"] };
        var sut = new StubProvider(options,
            Handle("a.md", "a.md"),
            Handle("b.yaml", "b.yaml"),
            Handle("c.pdf", "c.pdf"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_PredicateFilter_ExcludesMatchedPaths()
    {
        var options = new TestOptions { Filter = id => !id.StartsWith("internal/", StringComparison.Ordinal) };
        var sut = new StubProvider(options,
            Handle("docs/guide.md", "guide.md"),
            Handle("internal/secret.md", "secret.md"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("docs/guide.md", results[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsForwardedFromHandle()
    {
        var sut = new StubProvider(new TestOptions(),
            Handle("file.md", "file.md", etag: "etag-abc"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("etag-abc", results[0].ETag);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilterIsCaseInsensitive()
    {
        var options = new TestOptions { Extensions = [".MD"] };
        var sut = new StubProvider(options,
            Handle("readme.md", "readme.md"),
            Handle("notes.MD", "notes.MD"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetFilesAsync_BothFiltersApplied_ExcludesOnEither()
    {
        var options = new TestOptions
        {
            Extensions = [".md"],
            Filter = id => !id.StartsWith("internal/", StringComparison.Ordinal),
        };
        var sut = new StubProvider(options,
            Handle("docs/guide.md", "guide.md"),
            Handle("internal/secret.md", "secret.md"),
            Handle("readme.yaml", "readme.yaml"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("docs/guide.md", results[0].Id);
    }
}
