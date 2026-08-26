using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DataProviders;
using Rag.NET.Ingestion;
using Rag.NET.Ingestion.Behaviors;
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
            .Returns(entries.Select(e => Result<FileEntry, RagError>.Success(new FileEntry(
                Id: new EntryId(e.id),
                FileName: e.fileName,
                OpenContentAsync: _ => Task.FromResult<Stream>(
                    new MemoryStream(Encoding.UTF8.GetBytes(e.content))),
                ETag: e.etag))).ToAsyncEnumerable());
        return provider;
    }

    [Fact]
    public async Task IngestFromProviderAsync_NoHashStore_IngestsAllFiles()
    {
        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", null),
            ("id-2", "b.txt", "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.IngestedCount);
        Assert.Equal(0, result.SkippedCount);
        _ = await _pipeline.Received(2).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_ETagMatch_SkipsFile()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("id-1"), etag: "etag-abc", hash: "any", TestContext.Current.CancellationToken);
        var provider = MakeProvider(("id-1", "a.txt", "hello", "etag-abc"));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"), hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(1, result.SkippedCount);
        _ = await _pipeline.DidNotReceive().IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_HashMatch_SkipsIngestButRefreshesETag()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        // SHA-256 of "hello" in hex
        var helloHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("hello"u8.ToArray()));
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("id-1"), etag: "old-etag", hash: helloHash, TestContext.Current.CancellationToken);

        var provider = MakeProvider(("id-1", "a.txt", "hello", "new-etag"));
        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"), hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(1, result.SkippedCount);
        // ETag should be refreshed
        Assert.Equal("new-etag", await hashStore.GetETagAsync(new ProviderId("prov"), new EntryId("id-1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFromProviderAsync_NewFile_IngestsAndStoresHash()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"), hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        var hash = await hashStore.GetHashAsync(new ProviderId("prov"), new EntryId("id-1"), TestContext.Current.CancellationToken);
        Assert.NotNull(hash);
        _ = await _pipeline.Received(1).IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupModeFull_DeletesDisappearedDocuments()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("old-id"), null, "old-hash", TestContext.Current.CancellationToken);

        var provider = MakeProvider(("new-id", "new.txt", "content", null));
        var result = await _pipeline.IngestFromProviderAsync(
            provider, new ProviderId("prov"), hashStore: hashStore, cleanupMode: CleanupMode.Full,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeletedCount);
        await _pipeline.Received(1).DeleteAsync("old-id", Arg.Any<CancellationToken>());
        Assert.Null(await hashStore.GetHashAsync(new ProviderId("prov"), new EntryId("old-id"), TestContext.Current.CancellationToken));
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
        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(capturedMetadata);
        Assert.Equal("id-1", capturedMetadata[0].DocumentId);
        Assert.Equal("report.pdf", capturedMetadata[0].FileName);
    }

    /// <summary>
    /// The question the typed-metadata design has to answer (#91): a custom data provider
    /// submits a number, a boolean and a date on <see cref="FileEntry.Metadata"/> — every hop
    /// (BuildMetadata → <see cref="DocumentMetadata.Tags"/> → <see cref="MetadataBehavior"/> →
    /// <see cref="TextChunk.Metadata"/>) must keep the kind, because losing it at any hop
    /// re-creates the stringly-typed flattening this design removes. Also pins that a
    /// chunk-level value set before <see cref="MetadataBehavior"/> runs still wins over the
    /// document tag (TryAdd precedence), typed or not.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_TypedEntryMetadata_SurvivesToChunkMetadataTyped()
    {
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var capturedMetadata = new List<DocumentMetadata>();
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Do<DocumentMetadata>(m => capturedMetadata.Add(m)),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("typed-1"), ChunksStored = 1 })));

        var provider = MakeProviderWithMetadata(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["rating"] = 4.5,
            ["revision"] = 7,
            ["published"] = true,
            ["reviewed_at"] = reviewedAt,
        });
        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("crm"),
            cancellationToken: TestContext.Current.CancellationToken);

        // First hop: the provider's values reach DocumentMetadata.Tags with their kinds intact.
        var tags = Assert.Single(capturedMetadata).Tags;
        Assert.Equal(MetadataValueKind.Number, tags["rating"].Kind);

        // Last hop: the real MetadataBehavior merges the tags onto chunks, still typed.
        var ctx = new IngestionContext
        {
            Stream = Stream.Null,
            Metadata = capturedMetadata[0],
            GetNextBm25DocId = () => 0,
        };
        ctx.Chunks.Add(new TextChunk { Text = "chunk", DocumentId = new DocumentId("typed-1"), ChunkIndex = 0 });
        ctx.Chunks.Add(new TextChunk
        {
            Text = "chunk with its own value",
            DocumentId = new DocumentId("typed-1"),
            ChunkIndex = 1,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["revision"] = "chunk-level" },
        });
        await new MetadataBehavior().HandleAsync(ctx, TestContext.Current.CancellationToken,
            static (c, _) => ValueTask.FromResult(new IngestionResult
            {
                DocumentId = c.Metadata.DocumentId,
                ChunksStored = 0,
            }));

        var merged = ctx.Chunks[0].Metadata;
        Assert.Equal(MetadataValueKind.Number, merged["rating"].Kind);
        Assert.Equal(4.5, merged["rating"].NumberValue);
        Assert.Equal((MetadataValue)7, merged["revision"]);
        Assert.True(merged["published"].BooleanValue);
        Assert.Equal(reviewedAt, merged["reviewed_at"].DateTimeOffsetValue);
        Assert.Equal((MetadataValue)"crm", merged[ReservedMetadataKeys.ProviderId]);

        // TryAdd precedence: the chunk-level value survives; the document tag does not clobber it.
        Assert.Equal((MetadataValue)"chunk-level", ctx.Chunks[1].Metadata["revision"]);
    }

    [Fact]
    public async Task IngestFromProviderAsync_IngestThrows_AppendsToErrorsAndContinues()
    {
        // id-1 returns failure, id-2 succeeds — both must be attempted
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m => string.Equals(m!.DocumentId, "id-1", StringComparison.Ordinal)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Failure(
                new RagError.StorageFailed(new InvalidOperationException("simulated failure")))));

        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m => string.Equals(m!.DocumentId, "id-2", StringComparison.Ordinal)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("id-2"), ChunksStored = 1 })));

        var provider = MakeProvider(
            ("id-1", "fail.txt", "hello", null),
            ("id-2", "ok.txt",   "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.IngestedCount);   // id-2 ingested
        Assert.Equal(1, result.FailedCount);     // id-1 threw
        Assert.Equal(0, result.SkippedCount);    // nothing was up to date; #355
        Assert.Equal(2, result.IngestedCount + result.FailedCount); // both entries were attempted
        Assert.Single(result.Errors);
        Assert.IsType<RagError.StorageFailed>(result.Errors[0]);
    }

    [Fact]
    public async Task StopOnFirstError_StopsInsteadOfAttemptingEveryRemainingEntry()
    {
        // #355's case: the index is missing, so every entry fails for the same reason. Without
        // this option a 5,000-URL sitemap does 5,000 doomed round trips.
        FailEveryIngest();

        var provider = MakeProvider(
            ("id-1", "a.txt", "one",   null),
            ("id-2", "b.txt", "two",   null),
            ("id-3", "c.txt", "three", null));

        var result = await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            options: new IngestionOptions { StopOnFirstError = true },
            cancellationToken: TestContext.Current.CancellationToken);

        // One failure, and — the point of the option — the other two were never attempted. The
        // counts alone cannot show that: three entries go in and one outcome comes back, which
        // reads the same as a provider that only listed one. StopOnFirstError_ReportsTheEntries-
        // ItNeverReached asserts the missing two by name.
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(0, result.SkippedCount);
        // Discarded: IngestAsync returns a Result, and the analyzer requires one be observed.
        _ = await _pipeline.Received(1).IngestAsync(
            Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
            Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopOnFirstError_Unset_KeepsTodaysBehaviourOfAttemptingEverything()
    {
        // The default has to be unchanged: one malformed document in a crawl of thousands must not
        // abandon the rest.
        FailEveryIngest();

        var provider = MakeProvider(
            ("id-1", "a.txt", "one",   null),
            ("id-2", "b.txt", "two",   null),
            ("id-3", "c.txt", "three", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.FailedCount);
        Assert.Equal(0, result.IngestedCount);
        _ = await _pipeline.Received(3).IngestAsync(
            Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
            Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopOnFirstError_SuppressesFullCleanup_AndSaysSo()
    {
        // The hazard the option creates. Cleanup deletes what this run did not see, and a run that
        // stopped early did not see most of the provider. Deleting on that basis would remove
        // documents still present at the source — data loss caused by an error-handling option.
        FailEveryIngest();

        var hashStore = new SqliteContentHashStore(_dbPath);
        // Present in the store, absent from the provider — exactly what Full cleanup deletes.
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("gone"), null, "old-hash",
            TestContext.Current.CancellationToken);

        var provider = MakeProvider(
            ("id-1", "a.txt", "one", null),
            ("id-2", "b.txt", "two", null));

        var result = await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            hashStore: hashStore,
            options: new IngestionOptions { StopOnFirstError = true },
            cleanupMode: CleanupMode.Full,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DeletedCount);
        await _pipeline.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // And it is still there: the whole point is that the unvisited document survives.
        Assert.NotNull(await hashStore.GetHashAsync(new ProviderId("prov"), new EntryId("gone"),
            TestContext.Current.CancellationToken));

        // Not silent: a caller who asked for Full cleanup and got no deletions is owed the reason.
        var validation = Assert.Single(result.Errors.OfType<RagError.ValidationFailed>());
        Assert.Contains(validation.Failures, f =>
            string.Equals(f.PropertyName, nameof(IngestionOptions.StopOnFirstError), StringComparison.Ordinal));
    }

    // ---- #395: the result names the entries, not just how many there were ----

    [Fact]
    public async Task Ingested_NamesTheEntries_NotJustHowManyThereWere()
    {
        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", "etag-a"),
            ("id-2", "b.txt", "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        // The ask in #395: say which files, so a caller does not have to hook IProgress to find out.
        Assert.Equal(["a.txt", "b.txt"], result.Ingested.Select(e => e.FileName), StringComparer.Ordinal);
        Assert.Equal([new EntryId("id-1"), new EntryId("id-2")], result.Ingested.Select(e => e.Id));
        Assert.Equal("etag-a", result.Ingested[0].ETag);
        Assert.Null(result.Ingested[1].ETag);
    }

    [Fact]
    public async Task Skipped_NamesTheEntryThatWasAlreadyUpToDate()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("id-1"), etag: "etag-abc",
            hash: "any", TestContext.Current.CancellationToken);

        var provider = MakeProvider(
            ("id-1", "unchanged.txt", "hello", "etag-abc"),
            ("id-2", "fresh.txt", "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            hashStore: hashStore, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("unchanged.txt", Assert.Single(result.Skipped).FileName);
        Assert.Equal("fresh.txt", Assert.Single(result.Ingested).FileName);
    }

    [Fact]
    public async Task Failed_NamesTheEntryThatThrew()
    {
        FailEveryIngest();
        var provider = MakeProvider(("id-1", "broken.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        var failed = Assert.Single(result.Failed);
        Assert.Equal("broken.txt", failed.FileName);
        Assert.Equal(new EntryId("id-1"), failed.Id);
    }

    [Fact]
    public async Task StopOnFirstError_ReportsTheEntriesItNeverReached()
    {
        // The gap that made counts insufficient. Stopping at the first error leaves the remaining
        // entries in no list at all, so a run that stopped after one of 5,000 URLs is
        // indistinguishable from a run that only had one URL to do. NotAttempted is the difference.
        FailEveryIngest();

        var provider = MakeProvider(
            ("id-1", "a.txt", "one",   null),
            ("id-2", "b.txt", "two",   null),
            ("id-3", "c.txt", "three", null));

        var result = await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            options: new IngestionOptions { StopOnFirstError = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("a.txt", Assert.Single(result.Failed).FileName);
        Assert.Equal(["b.txt", "c.txt"], result.NotAttempted.Select(e => e.FileName), StringComparer.Ordinal);
        // And they really were never touched — named, not merely re-labelled.
        _ = await _pipeline.Received(1).IngestAsync(
            Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
            Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunThatCompletes_LeavesNotAttemptedEmpty()
    {
        // The other half: NotAttempted must not become a dumping ground that is non-empty whenever
        // anything failed. Every entry here was reached, one of them just failed.
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(
                string.Equals(ci.ArgAt<DocumentMetadata>(1).DocumentId.Value, "id-2", StringComparison.Ordinal)
                    ? Result<IngestionResult, RagError>.Failure(new RagError.StorageFailed(new InvalidOperationException("boom")))
                    : Result<IngestionResult, RagError>.Success(
                        new IngestionResult { DocumentId = ci.ArgAt<DocumentMetadata>(1).DocumentId, ChunksStored = 1 })));

        var provider = MakeProvider(
            ("id-1", "a.txt", "one",   null),
            ("id-2", "b.txt", "two",   null),
            ("id-3", "c.txt", "three", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.NotAttempted);
        Assert.Equal(["a.txt", "c.txt"], result.Ingested.Select(e => e.FileName), StringComparer.Ordinal);
        Assert.Equal("b.txt", Assert.Single(result.Failed).FileName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryListedEntry_LandsInExactlyOneList(bool stopOnFirstError)
    {
        // The invariant that makes the result a statement about the whole run rather than a set of
        // loosely related tallies: the four lists partition what the provider listed, whether or
        // not the run went the distance.
        FailEveryIngest();

        var provider = MakeProvider(
            ("id-1", "a.txt", "one",   null),
            ("id-2", "b.txt", "two",   null),
            ("id-3", "c.txt", "three", null));

        var result = await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            options: new IngestionOptions { StopOnFirstError = stopOnFirstError },
            cancellationToken: TestContext.Current.CancellationToken);

        var everywhere = result.Ingested
            .Concat(result.Skipped)
            .Concat(result.Failed)
            .Concat(result.NotAttempted)
            .Select(e => e.Id)
            .ToList();

        Assert.Equal(3, result.ListedCount);
        Assert.Equal(3, everywhere.Count);            // nothing lost
        Assert.Equal(3, everywhere.ToHashSet().Count); // and nothing double-counted
    }

    [Fact]
    public async Task Deleted_NamesTheDocumentByIdAlone_BecauseNothingListedItThisRun()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("gone"), null, "old-hash",
            TestContext.Current.CancellationToken);

        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            hashStore: hashStore, cleanupMode: CleanupMode.Full,
            cancellationToken: TestContext.Current.CancellationToken);

        var deleted = Assert.Single(result.Deleted);
        Assert.Equal(new EntryId("gone"), deleted.Id);
        // No name, and that is the honest answer: the provider did not list this document, so this
        // run never held a FileEntry for it. Anything else here would be invented.
        Assert.Null(deleted.FileName);
        // It is also not one of the listed entries.
        Assert.Equal(1, result.ListedCount);
    }

    [Fact]
    public void ProviderEntryOutcome_CarriesNoContentCallback()
    {
        // Guards #395's design decision against a well-meaning revert to FileEntry. A finished
        // run's report must not hand back a delegate that opens content from a source that may
        // already be gone — and for a deleted document there is no FileEntry to hand back at all.
        var properties = typeof(ProviderEntryOutcome).GetProperties();

        Assert.DoesNotContain(properties, p => typeof(Delegate).IsAssignableFrom(p.PropertyType));
        Assert.All(
            typeof(ProviderIngestionResult).GetProperties()
                .Where(p => p.PropertyType.IsGenericType
                    && p.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                .Where(p => p.PropertyType.GetGenericArguments()[0] != typeof(RagError)),
            p => Assert.Equal(typeof(ProviderEntryOutcome), p.PropertyType.GetGenericArguments()[0]));
    }

    private void FailEveryIngest() =>
        _pipeline.IngestAsync(
                Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Failure(
                new RagError.StorageFailed(new InvalidOperationException("index does not exist")))));

    [Fact]
    public async Task CleanupModeFull_WithoutAHashStore_DeletesNothingAndSaysSo()
    {
        // #394: a sitemap page excluded after indexing stayed in the index, and the run reported
        // success. Cleanup compares what this run saw against the ids the store recorded earlier —
        // with no store there is no history, so nothing can ever be found missing. It used to
        // return silently, which is indistinguishable from "there was nothing to remove".
        var provider = MakeProvider(("id-1", "a.txt", "one", null));

        var result = await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            hashStore: null,
            cleanupMode: CleanupMode.Full,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal(0, result.DeletedCount);

        var validation = Assert.Single(result.Errors.OfType<RagError.ValidationFailed>());
        Assert.Contains(validation.Failures, f =>
            string.Equals(f.PropertyName, "cleanupMode", StringComparison.Ordinal));
        Assert.Contains(validation.Failures, f =>
            f.ErrorMessage.Contains("hashStore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupModeFull_WithAHashStore_RemovesWhatTheProviderNoLongerLists()
    {
        // The other half, and the mechanism #394 actually needs: an entry the provider stops
        // listing — because a filter now excludes it, or because it was deleted at the source — is
        // removed. The provider does not have to say anything was excluded; not listing it is the
        // whole signal.
        var hashStore = new SqliteContentHashStore(_dbPath);
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("excluded-later"), null, "hash",
            TestContext.Current.CancellationToken);

        var provider = MakeProvider(("id-1", "a.txt", "one", null));

        var result = await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            hashStore: hashStore,
            cleanupMode: CleanupMode.Full,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeletedCount);
        await _pipeline.Received(1).DeleteAsync("excluded-later", Arg.Any<CancellationToken>());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupModeNone_DoesNotDeleteDisappearedDocuments()
    {
        var hashStore = new SqliteContentHashStore(_dbPath);
        // "old-id" exists in the store but will not appear from the provider
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("old-id"), null, "old-hash", TestContext.Current.CancellationToken);

        var provider = MakeProvider(("new-id", "new.txt", "content", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            hashStore: hashStore,
            cleanupMode: CleanupMode.None,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DeletedCount);
        await _pipeline.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // "old-id" must still be in the store
        Assert.NotNull(await hashStore.GetHashAsync(new ProviderId("prov"), new EntryId("old-id"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestFromProviderAsync_NullETag_HashMatch_DoesNotWriteHashStore()
    {
        var hashStore = Substitute.For<IContentHashStore>();

        // Pre-compute SHA-256 of "hello" — same as what the provider will return
        var helloHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("hello"u8.ToArray()));
        hashStore.GetETagAsync(new ProviderId("prov"), new EntryId("id-1"), Arg.Any<CancellationToken>()).Returns((string?)null);
        hashStore.GetHashAsync(new ProviderId("prov"), new EntryId("id-1"), Arg.Any<CancellationToken>()).Returns(helloHash);

        // Provider returns null ETag — content unchanged (hash matches)
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            hashStore: hashStore,
            cancellationToken: TestContext.Current.CancellationToken);

        // SetAsync must NOT be called — there is no new ETag to store and hash already matches
        await hashStore.DidNotReceive().SetAsync(
            Arg.Any<ProviderId>(), Arg.Any<EntryId>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_CleanupDeleteThrows_ErrorIsRecordedButProcessingContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        var hashStore = new SqliteContentHashStore(_dbPath);

        // Pre-register "id-old" as a known file from a previous run
        await hashStore.SetAsync(new ProviderId("prov"), new EntryId("id-old"), etag: null, hash: "oldhash", ct);

        // Provider returns only "id-new" — "id-old" has disappeared and should be cleaned up
        var provider = MakeProvider(("id-new", "b.txt", "world", null));

        // DeleteAsync throws for "id-old"
        _pipeline.DeleteAsync("id-old", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("delete failed")));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            hashStore: hashStore,
            cleanupMode: CleanupMode.Full,
            cancellationToken: ct);

        // Error is recorded
        Assert.Contains(result.Errors, e => e is RagError.StorageFailed);
        // Processing continued — id-new was ingested
        _ = await _pipeline.Received(1).IngestAsync(
            Arg.Any<Stream>(), Arg.Is<DocumentMetadata>(m => m!.DocumentId.Equals(new DocumentId("id-new"))),
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
        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            options: options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, result.IngestedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    /// <summary>
    /// Raises <paramref name="highWaterMark"/> to <paramref name="candidate"/> if it is larger.
    /// A compare-and-swap loop is required: <c>Interlocked.Exchange(ref max, Math.Max(max, current))</c>
    /// reads <c>max</c> outside the atomic write, so two racing callers can lose an update.
    /// </summary>
    private static void RecordHighWaterMark(ref int highWaterMark, int candidate)
    {
        var observed = Volatile.Read(ref highWaterMark);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref highWaterMark, candidate, observed);
            if (prior == observed)
                break;
            observed = prior;
        }
    }

    [Fact]
    public async Task IngestFromProviderAsync_ParallelIngestion_RunsConcurrently()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var gate = new SemaphoreSlim(0);

        // Completes the moment a second ingestion is inside the fake alongside the first —
        // exactly the property asserted below. Gating on the observation instead of sleeping
        // makes the test deterministic: a fixed delay could elapse before any task reached
        // the gate on a loaded machine, which made this test flaky (~1 run in 11).
        var concurrencyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var current = Interlocked.Increment(ref concurrentCount);
                RecordHighWaterMark(ref maxConcurrent, current);

                if (current > 1)
                    concurrencyObserved.TrySetResult();

                await gate.WaitAsync(TestContext.Current.CancellationToken);
                Interlocked.Decrement(ref concurrentCount);
                return Result<IngestionResult, RagError>.Success(
                    new IngestionResult { DocumentId = new DocumentId("x"), ChunksStored = 1 });
            });

        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", null),
            ("id-2", "b.txt", "world", null),
            ("id-3", "c.txt", "foo", null));

        var ingestTask = _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            options: new IngestionOptions { MaxDegreeOfParallelism = 3 },
            cancellationToken: TestContext.Current.CancellationToken);

        // Wait for concurrency to actually be observed rather than assuming it by now.
        // The timeout is a failure bound only — it is never part of the success path — so a
        // serialised pipeline fails here with a clear timeout instead of hanging.
        await concurrencyObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Release all
        gate.Release(3);

        var result = await ingestTask;

        Assert.Equal(3, result.IngestedCount);
        Assert.True(maxConcurrent > 1, $"Expected concurrent ingestion but maxConcurrent was {maxConcurrent}");
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
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-1"),
                    FileName: "doc.txt",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    Metadata: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["source"]  = "entry-value",   // entry overrides base for "source"
                        ["extra"]   = "entry-extra",
                    }))
            }.ToAsyncEnumerable());

        var baseMetadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("id-1"),
            FileName   = "base.pdf",
            Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["source"]    = "base-value",
                ["base-only"] = "base-only-value",
            }
        };

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            baseMetadata: baseMetadata,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(capturedMetadata);
        var tags = capturedMetadata[0].Tags!;
        Assert.Equal("entry-value",     tags["source"]);     // entry overrides base
        Assert.Equal("base-only-value", tags["base-only"]);  // base tag forwarded
        Assert.Equal("entry-extra",     tags["extra"]);      // entry-only tag included
    }

    /// <summary>
    /// <c>baseMetadata.CreatedAt</c> is supplied once per <c>IngestFromProviderAsync</c> call —
    /// a batch-level override, not a per-document timestamp — but <c>BuildMetadata</c> must still
    /// forward it onto every document's <see cref="DocumentMetadata"/>, exactly like
    /// <see cref="DocumentMetadata.ContentType"/> already is.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_BaseMetadataCreatedAt_ReachesIngestedDocument()
    {
        var capturedMetadata = CaptureIngestedMetadata();
        var provider = MakeProviderWithMetadata(
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["extra"] = "entry-extra" });

        var createdAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseMetadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("id-1"),
            FileName   = "base.pdf",
            CreatedAt  = createdAt,
        };

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            baseMetadata: baseMetadata,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(capturedMetadata);
        Assert.Equal(createdAt, capturedMetadata[0].CreatedAt);
    }

    /// <summary>
    /// Phase 4.10 Task 1: <c>FileEntry.CreatedAt</c>/<c>UpdatedAt</c> are per-entry, connector-set
    /// timestamps — distinct from <c>baseMetadata.CreatedAt</c>, which is a batch-level default
    /// supplied once per <c>IngestFromProviderAsync</c> call. Both must reach the ingested
    /// document's <see cref="DocumentMetadata"/> unchanged.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_FileEntryTimestamps_ReachIngestedDocument()
    {
        var captured = CaptureIngestedMetadata();
        var createdAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Constructed inline (not via a hand-written helper taking DateTime? parameters):
        // Roslynator RCS1242 and EPS05 disagree about whether such a parameter needs `in`, so
        // the timestamps are threaded straight from locals into the record's own constructor.
        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-1"),
                    FileName: "doc.txt",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    CreatedAt: createdAt,
                    UpdatedAt: updatedAt)),
            }.ToAsyncEnumerable());

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(captured);
        Assert.Equal(createdAt, metadata.CreatedAt);
        Assert.Equal(updatedAt, metadata.UpdatedAt);
    }

    /// <summary>
    /// <c>RssDataProvider</c>, <c>SitemapDataProvider</c> and <c>WebCrawlerDataProvider</c>
    /// (in <c>Rag.NET.DataProviders.Web</c>) implement <see cref="IFileContentProvider"/>
    /// directly and construct <see cref="FileEntry"/> themselves — they never touch
    /// <c>FileHandle</c> or <c>FileContentProviderBase</c>. Missing this path is invisible: if
    /// <c>BuildMetadata</c> only ever forwarded a timestamp reached via that base class (or only
    /// from <c>baseMetadata</c>), a connector on this path would silently never produce a
    /// timestamp — indistinguishable from a connector that truly has none. This test uses a bare
    /// <see cref="IFileContentProvider"/> implementation — not <c>FileContentProviderBase</c>,
    /// not a mock — to prove the direct path threads timestamps through.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_DirectIFileContentProviderImplementation_TimestampsReachIngestedDocument()
    {
        var captured = CaptureIngestedMetadata();
        var createdAt = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var provider = new DirectFileContentProvider { CreatedAt = createdAt, UpdatedAt = updatedAt };

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(captured);
        Assert.Equal(createdAt, metadata.CreatedAt);
        Assert.Equal(updatedAt, metadata.UpdatedAt);
    }

    /// <summary>
    /// A real (non-mock, non-<c>FileContentProviderBase</c>) <see cref="IFileContentProvider"/>
    /// implementation, shaped like the web connectors that construct <see cref="FileEntry"/>
    /// without ever going through <c>FileHandle</c>. Timestamps are plain <c>init</c> properties
    /// set by object initialiser, not constructor parameters — Roslynator RCS1242 and EPS05
    /// disagree about whether a hand-written method parameter of type <c>DateTime?</c> needs
    /// <c>in</c>, and property accessors sidestep that conflict entirely.
    /// </summary>
    private sealed class DirectFileContentProvider : IFileContentProvider
    {
        public DateTime? CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }

        public async IAsyncEnumerable<Result<FileEntry, RagError>> GetFilesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Result<FileEntry, RagError>.Success(new FileEntry(
                Id: new EntryId("id-1"),
                FileName: "doc.txt",
                OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                CreatedAt: CreatedAt,
                UpdatedAt: UpdatedAt));
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One entry carrying <paramref name="metadata"/> — enough to prove a per-entry metadata
    /// rule, and single-entry so an escaping exception is unambiguous.
    /// </summary>
    private static IFileContentProvider MakeProviderWithMetadata(IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-1"),
                    FileName: "doc.txt",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    Metadata: metadata)),
            }.ToAsyncEnumerable());
        return provider;
    }

    private List<DocumentMetadata> CaptureIngestedMetadata()
    {
        var captured = new List<DocumentMetadata>();
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Do<DocumentMetadata>(m => captured.Add(m)),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("id-1"), ChunksStored = 1 })));
        return captured;
    }

    [Theory]
    [InlineData(ReservedMetadataKeys.DocumentId)]
    [InlineData(ReservedMetadataKeys.FileName)]
    [InlineData(ReservedMetadataKeys.CreatedAt)]
    [InlineData(ReservedMetadataKeys.ProviderId)]
    [InlineData(ReservedMetadataKeys.ParentKey)]
    [InlineData(ReservedMetadataKeys.AllowedRoles)]
    [InlineData(ReservedMetadataKeys.TrustLevel)]
    public async Task BuildMetadata_EntryTagCollidingWithReservedKey_Throws(string reservedKey)
    {
        var provider = MakeProviderWithMetadata(
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { [reservedKey] = "connector-value" });

        var ex = await Assert.ThrowsAsync<ReservedMetadataKeyException>(() =>
            _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(reservedKey, ex.Key);
        Assert.Contains(reservedKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("prov", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard must not be swallowed by <c>ProcessEntryAsync</c>'s per-entry catch: a
    /// collision is a programming error and has to reach the caller, not become one
    /// <see cref="RagError.StorageFailed"/> per document.
    /// </summary>
    [Fact]
    public async Task BuildMetadata_ReservedKeyCollision_IsNotDowngradedToAnEntryError()
    {
        var provider = MakeProviderWithMetadata(
            new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { [ReservedMetadataKeys.CreatedAt] = "1999" });

        await Assert.ThrowsAsync<ReservedMetadataKeyException>(() =>
            _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
                cancellationToken: TestContext.Current.CancellationToken));

        _ = await _pipeline.DidNotReceive().IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The documented contract is that the caller catches <see cref="ReservedMetadataKeyException"/>
    /// directly. <see cref="Parallel.ForEachAsync{TSource}(IEnumerable{TSource}, ParallelOptions, Func{TSource, CancellationToken, ValueTask})"/>
    /// faults its task with an <see cref="AggregateException"/> envelope, and awaiting unwraps
    /// exactly one level — so a future change that added a second envelope would silently break
    /// every documented <c>catch (ReservedMetadataKeyException)</c> in user code. This pins it
    /// on the multi-exception path, where several workers throw and the aggregate really holds
    /// more than one inner exception.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_ReservedKeyCollisionUnderParallelism_EscapesUnwrapped()
    {
        // 32 entries, every fourth colliding, 8 workers: enough concurrency that several
        // workers genuinely throw. Which entry loses the race is not deterministic; the
        // exception's type and Key are, because every collision uses the same reserved key.
        const int total = 32;
        var entries = new List<Result<FileEntry, RagError>>(total);
        for (var i = 0; i < total; i++)
        {
            var tags = i % 4 == 0
                ? new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { [ReservedMetadataKeys.CreatedAt] = "1999" }
                : new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["repo"] = "acme/widgets" };

            entries.Add(Result<FileEntry, RagError>.Success(new FileEntry(
                Id: new EntryId($"id-{i}"),
                FileName: $"doc-{i}.txt",
                OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                Metadata: tags)));
        }

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>()).Returns(entries.ToAsyncEnumerable());

        // Assert.ThrowsAsync matches the EXACT type — an AggregateException (or any other
        // envelope) fails here rather than sneaking through as a derived match.
        var ex = await Assert.ThrowsAsync<ReservedMetadataKeyException>(() =>
            _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
                options: new IngestionOptions { MaxDegreeOfParallelism = 8 },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ReservedMetadataKeys.CreatedAt, ex.Key);
        Assert.Null(ex.InnerException); // not a re-wrapped inner either
    }

    /// <summary>
    /// Pins the partial-completion behaviour the public XML doc describes: entries already
    /// processed when the collision surfaces stay ingested, because the method throws instead
    /// of unwinding the work it has done.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_ReservedKeyCollision_LeavesAlreadyProcessedEntriesIngested()
    {
        var ct = TestContext.Current.CancellationToken;

        // Deterministic ordering without sleeping: the colliding entry's content is not opened
        // — and so BuildMetadata is not reached — until the clean entry has finished ingesting.
        var cleanIngested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestedIds = new ConcurrentBag<string>();

        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.ArgAt<DocumentMetadata>(1).DocumentId.Value;
                ingestedIds.Add(id);
                cleanIngested.TrySetResult();
                return Task.FromResult(Result<IngestionResult, RagError>.Success(
                    new IngestionResult { DocumentId = new DocumentId(id), ChunksStored = 1 }));
            });

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Result<FileEntry, RagError>.Success(new FileEntry(
                Id: new EntryId("id-clean"),
                FileName: "clean.txt",
                OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                Metadata: new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["repo"] = "acme/widgets" })),
            Result<FileEntry, RagError>.Success(new FileEntry(
                Id: new EntryId("id-collide"),
                FileName: "collide.txt",
                // The timeout is a failure bound only, never part of the success path.
                OpenContentAsync: async _ =>
                {
                    await cleanIngested.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    return new MemoryStream("hi"u8.ToArray());
                },
                Metadata: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    [ReservedMetadataKeys.CreatedAt] = "1999",
                })),
        }.ToAsyncEnumerable());

        await Assert.ThrowsAsync<ReservedMetadataKeyException>(() =>
            _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
                options: new IngestionOptions { MaxDegreeOfParallelism = 2 },
                cancellationToken: ct));

        // The clean entry was ingested and stays ingested — the throw unwinds nothing.
        Assert.Equal("id-clean", Assert.Single(ingestedIds));
    }

    [Fact]
    public async Task BuildMetadata_NonReservedEntryTag_IsForwarded()
    {
        var captured = CaptureIngestedMetadata();
        var provider = MakeProviderWithMetadata(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["repo"] = "acme/widgets",
            ["change_status"] = "modified",
        });

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        var tags = Assert.Single(captured).Tags;
        Assert.Equal("acme/widgets", tags["repo"]);
        Assert.Equal("modified", tags["change_status"]);
    }

    /// <summary>
    /// The metadata produced by <c>BuildMetadata</c> here is exactly what a real pipeline would
    /// hand to <see cref="MetadataBehavior"/> next — this closes the gap between the two tested
    /// in isolation elsewhere: <c>IngestFromProviderTests</c> only checks the connector-side
    /// guard, and <c>MetadataBehaviorCreatedAtTests</c> only ever exercises the behavior with an
    /// explicit <see cref="DocumentMetadata.CreatedAt"/> already set. Neither proves what a
    /// provider entry that supplies no timestamp actually produces in chunk metadata.
    /// </summary>
    [Fact]
    public async Task IngestFromProvider_WithNoTimestampFromTheConnector_DoesNotFabricateACreationTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var captured = CaptureIngestedMetadata();
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: ct);

        var metadata = Assert.Single(captured);

        var chunkCtx = new IngestionContext
        {
            Stream           = Stream.Null,
            Metadata         = metadata,
            GetNextBm25DocId = () => 0,
        };
        chunkCtx.Chunks.Add(new TextChunk
        {
            Text       = "hello",
            DocumentId = metadata.DocumentId,
            ChunkIndex = 0,
        });

        var behavior = new MetadataBehavior();
        await behavior.HandleAsync(chunkCtx, ct,
            (c, _) => ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 }));

        Assert.False(
            chunkCtx.Chunks[0].Metadata.TryGetValue(ReservedMetadataKeys.CreatedAt, out var fabricated),
            $"Connector supplied no timestamp; chunk metadata must not fabricate one, but found '{fabricated}'.");
    }

    // provider_id is written centrally so all 21 connectors carry it without per-connector work.
    // The two ingest paths are separate branches of ProcessEntryAsync, so both are covered.

    [Fact]
    public async Task IngestFromProviderAsync_WritesProviderIdTag_NoHashStore()
    {
        var captured = CaptureIngestedMetadata();
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov-42"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal("prov-42", Assert.Single(captured).Tags[ReservedMetadataKeys.ProviderId]);
    }

    [Fact]
    public async Task IngestFromProviderAsync_WritesProviderIdTag_HashStorePath()
    {
        var captured = CaptureIngestedMetadata();
        var hashStore = new SqliteContentHashStore(_dbPath);
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov-42"),
            hashStore: hashStore, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal("prov-42", Assert.Single(captured).Tags[ReservedMetadataKeys.ProviderId]);
    }

    [Fact]
    public async Task IngestFromProviderAsync_ConnectorCannotShadowProviderIdTag()
    {
        var provider = MakeProviderWithMetadata(new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            [ReservedMetadataKeys.ProviderId] = "impostor",
        });

        var ex = await Assert.ThrowsAsync<ReservedMetadataKeyException>(() =>
            _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov-42"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ReservedMetadataKeys.ProviderId, ex.Key);
    }

    /// <summary>
    /// Caller-supplied base metadata is not reserved-key guarded (it is not connector code), so
    /// the central write happens last and stays authoritative.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_ProviderIdTagWinsOverBaseMetadata()
    {
        var captured = CaptureIngestedMetadata();
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));
        var baseMetadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("id-1"),
            FileName = "base.pdf",
            Tags = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                [ReservedMetadataKeys.ProviderId] = "stale",
            },
        };

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov-42"),
            baseMetadata: baseMetadata, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("prov-42", Assert.Single(captured).Tags[ReservedMetadataKeys.ProviderId]);
    }

    [Fact]
    public async Task IngestFromProviderAsync_ProviderHttpFailure_AppearsInErrors()
    {
        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Failure(
                    new RagError.HttpFailed(System.Net.HttpStatusCode.Unauthorized, "Unauthorized")),
            }.ToAsyncEnumerable());

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.IngestedCount);
        Assert.Single(result.Errors);
        var error = Assert.IsType<RagError.HttpFailed>(result.Errors[0]);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task IngestFromProviderAsync_MultipleProviderHttpFailures_AllAppearInErrors()
    {
        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Failure(new RagError.HttpFailed(System.Net.HttpStatusCode.Unauthorized, null)),
                Result<FileEntry, RagError>.Failure(new RagError.HttpFailed(System.Net.HttpStatusCode.Forbidden, null)),
            }.ToAsyncEnumerable());

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.IsType<RagError.HttpFailed>(e));
    }

    [Fact]
    public async Task IngestFromProviderAsync_MaxDegreeOfParallelismZero_FailsFastWithSingleValidationError()
    {
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            options: new IngestionOptions { MaxDegreeOfParallelism = 0 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.IngestedCount);
        var error = Assert.IsType<RagError.ValidationFailed>(Assert.Single(result.Errors));
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("MaxDegreeOfParallelism", StringComparison.OrdinalIgnoreCase));
        // Fail-fast: the provider is never enumerated.
        _ = provider.DidNotReceive().GetFilesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_MaxDegreeOfParallelismMinusOne_IsAcceptedAsUnbounded()
    {
        var provider = MakeProvider(
            ("id-1", "a.txt", "hello", null),
            ("id-2", "b.txt", "world", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            options: new IngestionOptions { MaxDegreeOfParallelism = -1 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.IngestedCount);
    }

    [Fact]
    public async Task IngestFromProviderAsync_EmbedBatchSizeZero_FailsFastWithoutEnumeratingProvider()
    {
        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            options: new IngestionOptions { EmbedBatchSize = 0 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.IngestedCount);
        var error = Assert.IsType<RagError.ValidationFailed>(Assert.Single(result.Errors));
        Assert.Contains(error.Failures, f => f.PropertyName.Contains("EmbedBatchSize", StringComparison.OrdinalIgnoreCase));
        _ = provider.DidNotReceive().GetFilesAsync(Arg.Any<CancellationToken>());
        _ = await _pipeline.DidNotReceive().IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(),
            Arg.Any<IngestionOptions?>(), Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestFromProviderAsync_MixedSuccessAndHttpFailure_IngestsSuccessesRecordsFailure()
    {
        var entry = new FileEntry(
            Id: new EntryId("id-ok"),
            FileName: "ok.txt",
            OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("content"u8.ToArray())),
            ETag: null);

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Success(entry),
                Result<FileEntry, RagError>.Failure(new RagError.HttpFailed(System.Net.HttpStatusCode.InternalServerError, null)),
            }.ToAsyncEnumerable());

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.IngestedCount);
        Assert.Single(result.Errors);
        Assert.IsType<RagError.HttpFailed>(result.Errors[0]);
    }

    /// <summary>
    /// Issue #95: a provider must be able to declare each entry's content type itself.
    /// <para>
    /// Before <see cref="FileEntry.ContentType"/> existed the only source was the batch-level
    /// <c>baseMetadata</c>, whose <c>DocumentId</c> and <c>FileName</c> are <c>required</c> — so
    /// declaring "these are PDFs" meant inventing an id and a filename the pipeline overwrites
    /// per entry. It also allowed one content type per call, which a provider yielding a PDF and
    /// a Markdown file cannot live with.
    /// </para>
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_EntryContentTypes_ReachDocumentsIndependently()
    {
        var captured = CaptureIngestedMetadata();

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-pdf"),
                    FileName: "guide.pdf",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    ContentType: "application/pdf")),
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-md"),
                    FileName: "notes.md",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    ContentType: "text/markdown")),
            }.ToAsyncEnumerable());

        await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, captured.Count);
        Assert.Contains(captured, m => string.Equals(m.ContentType, "application/pdf", StringComparison.Ordinal));
        Assert.Contains(captured, m => string.Equals(m.ContentType, "text/markdown", StringComparison.Ordinal));
    }

    /// <summary>
    /// The entry wins over the batch default, matching how <c>Tags</c> and the timestamps already
    /// resolve — otherwise a caller could not override a batch default for one awkward file.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_EntryContentType_OverridesBaseMetadata()
    {
        var captured = CaptureIngestedMetadata();

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-1"),
                    FileName: "notes.md",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())),
                    ContentType: "text/markdown")),
            }.ToAsyncEnumerable());

        await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            baseMetadata: new DocumentMetadata
            {
                DocumentId = new DocumentId("ignored"),
                FileName = "ignored.pdf",
                ContentType = "application/pdf",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(captured);
        Assert.Equal("text/markdown", metadata.ContentType);
    }

    /// <summary>
    /// A provider that does not know its content type still says nothing and the batch-level
    /// default applies — #95's fix adds a way to be specific without removing the old one.
    /// </summary>
    [Fact]
    public async Task IngestFromProviderAsync_NoEntryContentType_FallsBackToBaseMetadata()
    {
        var captured = CaptureIngestedMetadata();

        var provider = Substitute.For<IFileContentProvider>();
        provider.GetFilesAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Result<FileEntry, RagError>.Success(new FileEntry(
                    Id: new EntryId("id-1"),
                    FileName: "doc.bin",
                    OpenContentAsync: _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray())))),
            }.ToAsyncEnumerable());

        await _pipeline.IngestFromProviderAsync(
            provider,
            new ProviderId("prov"),
            baseMetadata: new DocumentMetadata
            {
                DocumentId = new DocumentId("ignored"),
                FileName = "ignored.txt",
                ContentType = "application/pdf",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var metadata = Assert.Single(captured);
        Assert.Equal("application/pdf", metadata.ContentType);
    }

    /// <summary>
    /// An entry that throws is counted as <c>Failed</c>, never as <c>Skipped</c> (#355).
    /// </summary>
    /// <remarks>
    /// <c>Skipped</c> means "already up to date" — an ETag or content hash matched and there was
    /// nothing to do. Reporting a failure the same way told the reporter their sitemap ingest had
    /// skipped fifty entries when in fact the index did not exist and every one had thrown; the
    /// only way to tell was to read <c>Errors</c>. Two different outcomes wearing one name.
    /// </remarks>
    [Fact]
    public async Task IngestFromProviderAsync_EntryThrows_CountsFailedNotSkipped()
    {
        // A failure Result, not a thrown exception: the pipeline turns !IsSuccess into a throw
        // inside the per-entry try, which is the path that used to report Skipped.
        _pipeline.IngestAsync(Arg.Any<Stream>(), Arg.Any<DocumentMetadata>(), Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Failure(
                new RagError.StorageFailed(new InvalidOperationException("index not found")))));

        var provider = MakeProvider(("id-1", "a.txt", "hello", null));

        var result = await _pipeline.IngestFromProviderAsync(provider, new ProviderId("prov"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.IngestedCount);
        _ = Assert.Single(result.Errors);
    }
}
