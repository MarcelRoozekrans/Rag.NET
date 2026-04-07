using System.Text;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Tests.DataProviders;

public sealed class IngestFromProviderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-ingest-{Guid.NewGuid():N}.db");
    private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();

    public IngestFromProviderTests()
    {
        // Default: IngestAsync succeeds with an empty result unless overridden per-test.
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = ci.ArgAt<DocumentMetadata>(1).DocumentId, ChunksStored = 1 })));
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static IFileContentProvider MakeProvider(params (string id, string fileName, string content, string? etag)[] entries)
    {
        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(entries.Select(e => new FileEntry(
                Id: e.id,
                FileName: e.fileName,
                OpenContentAsync: _ => Task.FromResult<Stream>(
                    new MemoryStream(Encoding.UTF8.GetBytes(e.content))),
                ETag: e.etag)).ToAsyncEnumerable());
        return provider;
    }

    [Fact]
    public async Task IngestFromProviderAsync_NoHashStore_IngestsAllFiles()
    {
        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", null),
            ("id-2", "b.txt", "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Ingested);
        Assert.Equal(0, result.Skipped);
        _ = await _pipeline.Received(2).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_ETagMatch_SkipsFile()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync("prov", "id-1", etag: "etag-abc", hash: "any", TestContext.Current.CancellationToken);
        var provider = MakeProvider(("id-1", "a.txt", "hello", "etag-abc"));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov", hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Ingested);
        Assert.Equal(1, result.Skipped);
        _ = await _pipeline.DidNotReceive().IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_HashMatch_SkipsIngestButRefreshesETag()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        // SHA-256 of "hello" in hex
        var helloHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("hello"u8.ToArray()));
        await hashStore.SetAsync("prov", "id-1", etag: "old-etag", hash: helloHash, TestContext.Current.CancellationToken);

        var provider = MakeProvider(("id-1", "a.txt", "hello", "new-etag"));
        var result = await _pipeline.IngestFromProviderAsync(provider, "prov", hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Ingested);
        Assert.Equal(1, result.Skipped);
        // ETag should be refreshed
        Assert.Equal("new-etag", await hashStore.GetETagAsync("prov", "id-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFromProviderAsync_NewFile_IngestsAndStoresHash()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        await _pipeline.IngestFromProviderAsync(provider, "prov", hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        var hash = await hashStore.GetHashAsync("prov", "id-1", TestContext.Current.CancellationToken);
        Assert.NotNull(hash);
        _ = await _pipeline.Received(1).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupModeFull_DeletesDisappearedDocuments()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync("prov", "old-id", null, "old-hash", TestContext.Current.CancellationToken);

        var provider = MakeProvider(("new-id", "new.txt", "content", null));
        var result = await _pipeline.IngestFromProviderAsync(
            provider, "prov", hashStore: hashStore, cleanupMode: CleanupMode.Full,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Deleted);
        await _pipeline.Received(1).DeleteAsync("old-id", Arg.Any<CancellationToken>());
        Assert.Null(await hashStore.GetHashAsync("prov", "old-id", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFromProviderAsync_MetadataForwarded_DocumentIdIsEntryId()
    {
        var capturedMetadata = new List<DocumentMetadata>();
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Do<DocumentMetadata>(m => capturedMetadata.Add(m)),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("id-1"), ChunksStored = 1 })));

        var provider = MakeProvider(("id-1", "report.pdf", "content", null));
        await _pipeline.IngestFromProviderAsync(provider, "prov",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(capturedMetadata);
        Assert.Equal("id-1", capturedMetadata[0].DocumentId);
        Assert.Equal("report.pdf", capturedMetadata[0].FileName);
    }

    [Fact]
    public async Task IngestFromProviderAsync_IngestThrows_AppendsToErrorsAndContinues()
    {
        // id-1 returns failure, id-2 succeeds — both must be attempted
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m => string.Equals(m.DocumentId, "id-1", StringComparison.Ordinal)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Failure(
                new RagError.StorageFailed(new InvalidOperationException("simulated failure")))));

        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m => string.Equals(m.DocumentId, "id-2", StringComparison.Ordinal)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("id-2"), ChunksStored = 1 })));

        var provider = MakeProvider(
            ("id-1", "fail.txt", "hello", null),
            ("id-2", "ok.txt",   "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Ingested);                                     // id-2 ingested
        Assert.Equal(2, result.Ingested + result.Skipped); // both entries were attempted
        Assert.Single(result.Errors);
        Assert.Contains("id-1", result.Errors[0], StringComparison.Ordinal); // error message names the entry
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupModeNone_DoesNotDeleteDisappearedDocuments()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        // "old-id" exists in the store but will not appear from the provider
        await hashStore.SetAsync("prov", "old-id", null, "old-hash", TestContext.Current.CancellationToken);

        var provider = MakeProvider(("new-id", "new.txt", "content", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
            hashStore: hashStore,
            cleanupMode: CleanupMode.None,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Deleted);
        await _pipeline.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // "old-id" must still be in the store
        Assert.NotNull(await hashStore.GetHashAsync("prov", "old-id", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFromProviderAsync_NullETag_HashMatch_DoesNotWriteHashStore()
    {
        var hashStore = Substitute.For<IContentHashStore>();

        // Pre-compute SHA-256 of "hello" — same as what the provider will return
        var helloHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("hello"u8.ToArray()));
        hashStore.GetETagAsync("prov", "id-1", Arg.Any<CancellationToken>()).Returns((string?)null);
        hashStore.GetHashAsync("prov", "id-1", Arg.Any<CancellationToken>()).Returns(helloHash);

        // Provider returns null ETag — content unchanged (hash matches)
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        await _pipeline.IngestFromProviderAsync(provider, "prov",
            hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        // SetAsync must NOT be called — there is no new ETag to store and hash already matches
        await hashStore.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupDeleteThrows_ErrorIsRecordedButProcessingContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var hashStore = new SqliteContentHashStore(_dbPath);

        // Pre-register "id-old" as a known file from a previous run
        await hashStore.SetAsync("prov", "id-old", etag: null, hash: "oldhash", ct);

        // Provider returns only "id-new" — "id-old" has disappeared and should be cleaned up
        var provider = MakeProvider(("id-new", "b.txt", "world", null));

        // DeleteAsync throws for "id-old"
        _pipeline.DeleteAsync("id-old", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("delete failed")));

        var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
            hashStore: hashStore,
            cleanupMode: CleanupMode.Full,
            cancellationToken: ct);

        // Error is recorded
        Assert.Contains(result.Errors, e => e.Contains("id-old", StringComparison.Ordinal));
        // Processing continued — id-new was ingested
        _ = await _pipeline.Received(1).IngestAsync(
            Arg.Any<Stream>(), Arg.Is<DocumentMetadata>(m => m.DocumentId.Equals(new DocumentId("id-new"))),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), ct);
    }

    [Fact]
    public async Task IngestFromProviderAsync_ParallelIngestion_AllFilesIngested()
    {
        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", null),
            ("id-2", "b.txt", "world", null),
            ("id-3", "c.txt", "foo", null),
            ("id-4", "d.txt", "bar", null));

        var options = new IngestionOptions { MaxDegreeOfParallelism = 4 };
        var result = await _pipeline.IngestFromProviderAsync(provider, "prov",
            options: options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Ingested);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task IngestFromProviderAsync_MergesBaseAndEntryMetadataTags()
    {
        var capturedMetadata = new List<DocumentMetadata>();
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Do<DocumentMetadata>(m => capturedMetadata.Add(m)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("id-1"), ChunksStored = 1 })));

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new FileEntry(
                    Id: "id-1",
                    FileName: "doc.txt",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"]  = "entry-value",   // entry overrides base for "source"
                        ["extra"]   = "entry-extra",
                    })
            }.ToAsyncEnumerable());

        var baseMetadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("id-1"),
            FileName   = "base.pdf",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"]    = "base-value",
                ["base-only"] = "base-only-value",
            }
        };

        await _pipeline.IngestFromProviderAsync(provider, "prov",
            baseMetadata: baseMetadata,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(capturedMetadata);
        var tags = capturedMetadata[0].Tags!;
        Assert.Equal("entry-value",     tags["source"]);     // entry overrides base
        Assert.Equal("base-only-value", tags["base-only"]);  // base tag forwarded
        Assert.Equal("entry-extra",     tags["extra"]);      // entry-only tag included
    }
}
