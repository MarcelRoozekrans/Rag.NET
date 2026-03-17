using System.Text;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.DataProviders;

public sealed class IngestFromProviderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-ingest-{Guid.NewGuid():N}.db");
    private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();

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
        await _pipeline.Received(2).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
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
        await _pipeline.DidNotReceive().IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
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
        await _pipeline.Received(1).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
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
            .Returns(new IngestionResult { DocumentId = "id-1", ChunksStored = 1 });

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
        // id-1 throws, id-2 succeeds — both must be attempted
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m => string.Equals(m.DocumentId, "id-1", StringComparison.Ordinal)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IngestionResult>(new InvalidOperationException("simulated failure")));

        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m => string.Equals(m.DocumentId, "id-2", StringComparison.Ordinal)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new IngestionResult { DocumentId = "id-2", ChunksStored = 1 });

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
}
