using System.Runtime.CompilerServices;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.DataProviders.Tests;

public sealed class FileContentProviderBaseTests
{
    private sealed class StubProvider(
        CloudStorageOptions options,
        params FileHandle[] handles) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var h in handles)
                yield return Result<FileHandle, RagError>.Success(h);
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
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Equal(2, entries.Count);
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
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Single(entries);
        Assert.Equal("readme.md", entries[0].Id);
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
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Equal(3, entries.Count);
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
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Single(entries);
        Assert.Equal("docs/guide.md", entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_ETagIsForwardedFromHandle()
    {
        var sut = new StubProvider(new TestOptions(),
            Handle("file.md", "file.md", etag: "etag-abc"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Equal("etag-abc", entries[0].ETag);
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
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Equal(2, entries.Count);
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
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Single(entries);
        Assert.Equal("docs/guide.md", entries[0].Id);
    }

    [Fact]
    public async Task GetFilesAsync_HandleFailure_PropagatesAsFailureResult()
    {
        var failingProvider = new FailingStubProvider(new TestOptions());

        var results = await failingProvider.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(results);
        Assert.True(results[0].IsFailure);
        Assert.IsType<RagError.HttpFailed>(results[0].Error);
    }

    private sealed class FailingStubProvider(CloudStorageOptions options) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.ServiceUnavailable, null));
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
