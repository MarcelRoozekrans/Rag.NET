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

    private static FileHandle Handle(
        string id,
        string fileName,
        string? etag = null,
        IReadOnlyDictionary<string, MetadataValue>? metadata = null)
        => new(id, fileName, etag, _ => Task.FromResult<Stream>(new MemoryStream()), metadata);

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
    public async Task GetFilesAsync_MetadataIsForwardedFromHandle()
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["repo"] = "acme/widgets",
            ["change_status"] = "modified",
        };
        var sut = new StubProvider(new TestOptions(),
            Handle("file.md", "file.md", metadata: metadata));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        var forwarded = Assert.Single(entries).Metadata;
        Assert.NotNull(forwarded);
        Assert.Equal("acme/widgets", forwarded["repo"]);
        Assert.Equal("modified", forwarded["change_status"]);
    }

    /// <summary>
    /// Pins the convention: a connector with nothing to add leaves <c>Metadata</c> null, and the
    /// base class must not substitute an empty dictionary. One representation, not two.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_NullMetadataStaysNull()
    {
        var sut = new StubProvider(new TestOptions(), Handle("file.md", "file.md"));

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        Assert.Null(Assert.Single(entries).Metadata);
    }

    /// <summary>
    /// Phase 4.10 Task 1: <c>FileHandle.CreatedAt</c>/<c>UpdatedAt</c> must survive the
    /// <c>FileHandle</c> → <c>FileEntry</c> copy inside <see cref="FileContentProviderBase"/>,
    /// exactly like <c>ETag</c> and <c>Metadata</c> already do.
    /// </summary>
    [Fact]
    public async Task GetFilesAsync_CreatedAtAndUpdatedAtAreForwardedFromHandle()
    {
        var createdAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        // Constructed directly (not via the Handle() helper) so CreatedAt/UpdatedAt pass through
        // the record's own positional constructor rather than a hand-written wrapper — Roslynator
        // RCS1242 and EPS05 disagree about whether a hand-written method parameter of type
        // Nullable<DateTime> should take `in`, so this test sidesteps the conflict entirely.
        var handle = new FileHandle(
            "file.md", "file.md", null,
            _ => Task.FromResult<Stream>(new MemoryStream()),
            Metadata: null,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt);
        var sut = new StubProvider(new TestOptions(), handle);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var entries = results.Select(r => { Assert.True(r.IsSuccess, r.IsFailure ? $"Expected success but got failure: {r.Error}" : string.Empty); return r.Value; }).ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(createdAt, entry.CreatedAt);
        Assert.Equal(updatedAt, entry.UpdatedAt);
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

    [Fact]
    public async Task GetFilesAsync_MixedSuccessAndFailure_PropagatesBoth()
    {
        var sut = new MixedStubProvider(new TestOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.True(results[0].IsSuccess);
        Assert.Equal("a.md", results[0].Value.FileName);
        Assert.True(results[1].IsFailure);
        Assert.IsType<RagError.HttpFailed>(results[1].Error);
        Assert.True(results[2].IsSuccess);
        Assert.Equal("b.md", results[2].Value.FileName);
    }

    [Fact]
    public async Task GetFilesAsync_ExtensionFilter_StillFiltersSuccessesWhenFailuresMixed()
    {
        var options = new TestOptions { Extensions = [".md"] };
        var sut = new MixedWithNonMdStubProvider(options);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        // success (.md) → passes, failure → passes through, success (.yaml) → filtered out
        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsSuccess);
        Assert.Equal("doc.md", results[0].Value.FileName);
        Assert.True(results[1].IsFailure);
    }

    [Fact]
    public async Task GetFilesAsync_MultipleFailures_AllPropagated()
    {
        var sut = new MultiFailingStubProvider(new TestOptions());

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsFailure));
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

    private sealed class MixedStubProvider(CloudStorageOptions options) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Result<FileHandle, RagError>.Success(Handle("a/a.md", "a.md"));
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.InternalServerError, null));
            yield return Result<FileHandle, RagError>.Success(Handle("b/b.md", "b.md"));
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class MixedWithNonMdStubProvider(CloudStorageOptions options) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Result<FileHandle, RagError>.Success(Handle("doc.md", "doc.md"));
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.Unauthorized, null));
            yield return Result<FileHandle, RagError>.Success(Handle("config.yaml", "config.yaml"));
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class MultiFailingStubProvider(CloudStorageOptions options) : FileContentProviderBase(options)
    {
        protected override async IAsyncEnumerable<Result<FileHandle, RagError>> GetFileHandlesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.Unauthorized, null));
            yield return Result<FileHandle, RagError>.Failure(
                new RagError.HttpFailed(System.Net.HttpStatusCode.Forbidden, null));
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
