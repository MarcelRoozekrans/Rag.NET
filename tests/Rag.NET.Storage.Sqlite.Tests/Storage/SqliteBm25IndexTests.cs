using System.Globalization;
using Microsoft.Data.Sqlite;
using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class SqliteBm25IndexTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-test-{Guid.NewGuid():N}.db");
    private SqliteBm25Index? _sut;

    private SqliteBm25Index CreateSut(string collection = "test-coll")
    {
        _sut = new SqliteBm25Index(_dbPath, collection);
        return _sut;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static TextChunk MakeChunk(string docId, int idx, string text) => new()
    {
        Text = text, DocumentId = new DocumentId(docId), ChunkIndex = idx,
    };

    private static TextChunk FilterChunk(int index, string text, string tenant)
    {
        return new TextChunk
        {
            DocumentId = new DocumentId("doc-" + index.ToString(CultureInfo.InvariantCulture)),
            ChunkIndex = index,
            Text = text,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = tenant,
            },
        };
    }

    [Fact]
    public async Task Add_ThenRestart_SearchFindsChunk()
    {
        var sut = CreateSut();
        sut.Add(MakeChunk("doc-1", 0, "hello world"));
        await sut.DisposeAsync();

        // Simulate restart: create new instance pointing to same db
        _sut = new SqliteBm25Index(_dbPath, "test-coll");
        var results = _sut.Search("hello", topK: 5);
        Assert.Single(results);
        Assert.Equal("hello world", results[0].chunk.Text);
    }

    [Fact]
    public async Task Remove_ThenRestart_SearchFindsNothing()
    {
        var sut = CreateSut();
        sut.Add(MakeChunk("doc-1", 0, "hello world"));
        sut.Remove("doc-1");
        await sut.DisposeAsync();

        _sut = new SqliteBm25Index(_dbPath, "test-coll");
        var results = _sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task CollectionNameMismatch_WipesExistingData()
    {
        var sut = CreateSut("collection-A");
        sut.Add(MakeChunk("doc-1", 0, "hello world"));
        await sut.DisposeAsync();

        // New instance with different collection name → stale guard wipes data
        _sut = new SqliteBm25Index(_dbPath, "collection-B");
        var results = _sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Add_MultipleChunks_AllReturnedBySearch()
    {
        var sut = CreateSut();
        sut.Add(MakeChunk("doc-1", 0, "the quick brown fox"));
        sut.Add(MakeChunk("doc-2", 0, "the lazy dog"));

        var results = sut.Search("fox", topK: 5);
        Assert.Single(results); // only first chunk matches "fox"
        Assert.Equal("doc-1", results[0].chunk.DocumentId);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllChunks()
    {
        var sut = CreateSut();
        sut.Add(MakeChunk("doc-1", 0, "hello world"));
        sut.Add(MakeChunk("doc-2", 0, "foo bar"));

        await sut.ClearAsync(TestContext.Current.CancellationToken);

        var results = sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ClearAsync_ThenRestart_SearchFindsNothing()
    {
        var sut = CreateSut();
        sut.Add(MakeChunk("doc-1", 0, "hello world"));
        await sut.ClearAsync(TestContext.Current.CancellationToken);
        await sut.DisposeAsync();

        _sut = new SqliteBm25Index(_dbPath, "test-coll");
        var results = _sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task InitializeAsync_CanBeAwaited_ThenAddWorksWithoutBlockingInit()
    {
        var sut = CreateSut();

        // Should complete without blocking the thread-pool
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        // Subsequent operations use the already-initialised state
        sut.Add(MakeChunk("doc-1", 0, "hello world"));
        var results = sut.Search("hello", 5);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].chunk.DocumentId);
    }

    [Fact]
    public void Add_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            sut.Add(MakeChunk("doc-1", 0, "hello")));
    }

    [Fact]
    public void Search_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sut.Search("hello", 5));
    }

    [Fact]
    public async Task InitializeAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.InitializeAsync(TestContext.Current.CancellationToken));
    }

    // Follows the Add_ThenRestart_SearchFindsChunk shape above (a fresh instance over the same
    // db) rather than searching the instance that just added the chunks, which would exercise
    // only the one-line delegation to InMemoryBm25Index.Search the compiler already forces.
    // Restarting round-trips metadata through metadata_json and LoadIntoMemory (SqliteBm25Index.cs:202),
    // which silently substitutes an empty metadata dictionary on a deserialize failure -- a real
    // failure there would make every chunk fail every filter, and only a restart-based test can
    // see that.
    [Fact]
    public async Task Add_ThenRestart_SearchWithMetadataFilter_ExcludesNonMatchingChunks()
    {
        var sut = CreateSut();
        sut.Add(FilterChunk(1, "shared search term", "a"));
        sut.Add(FilterChunk(2, "shared search term", "b"));
        await sut.DisposeAsync();

        // Simulate restart: create new instance pointing to same db.
        _sut = new SqliteBm25Index(_dbPath, "test-coll");
        var results = _sut.Search(
            "shared search term",
            topK: 10,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }

    /// <summary>
    /// A document indexed after a restart is searchable, because the index allocates its own ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is #490, and it was silent.</b> <c>PipelineIngestor</c> used to allocate BM25 ids
    /// from a per-instance counter starting at 0, while <c>InitialiseCore</c> reloads the ids this
    /// index already persisted. After a restart the caller therefore handed over ids the index
    /// held, <c>InMemoryBm25Index.Add</c> hit <c>if (_docs.ContainsKey(docId)) return;</c>, and the
    /// chunk was dropped without an error, a log line or a return value anyone could check.
    /// </para>
    /// <para>
    /// <b>Measured before the fix: searching for the first document returned 1 hit and the second
    /// returned 0.</b> Worse, <c>Add</c> had already run <c>INSERT OR REPLACE</c>, so the persisted
    /// row held the NEW chunk under the OLD chunk's id while memory held the old one — the two
    /// stores disagreed until the next restart swapped which was visible.
    /// </para>
    /// <para>
    /// At shipped defaults with <c>UseSqlitePersistence</c> this meant every document ingested
    /// after a process restart was missing from keyword and hybrid search, which reads as a
    /// relevance problem rather than a missing document.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADocumentIndexedAfterAReopen_IsSearchable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ragnet-bm25-reopen-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = new SqliteBm25Index(path))
            {
                await first.InitializeAsync(TestContext.Current.CancellationToken);
                first.Add(MakeChunk("doc-a", 0, "alpha unique"));
            }

            SqliteConnection.ClearAllPools();

            using var second = new SqliteBm25Index(path);
            await second.InitializeAsync(TestContext.Current.CancellationToken);
            second.Add(MakeChunk("doc-b", 0, "bravo unique"));

            Assert.Single(second.Search("alpha", topK: 10));
            Assert.Single(second.Search("bravo", topK: 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Reopening seeds the allocator above every id it restored.</summary>
    /// <remarks>
    /// The mechanism behind the test above, asserted directly so a regression says which half
    /// broke: ids handed out after a reload must not revisit the reloaded range.
    /// </remarks>
    [Fact]
    public async Task ReopeningSeedsTheAllocator_AboveTheRestoredIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ragnet-bm25-seed-{Guid.NewGuid():N}.db");
        try
        {
            var firstIds = new List<int>();
            using (var first = new SqliteBm25Index(path))
            {
                await first.InitializeAsync(TestContext.Current.CancellationToken);
                for (var i = 0; i < 5; i++)
                    firstIds.Add(first.Add(MakeChunk("doc-a", i, $"chunk {i}")));
            }

            SqliteConnection.ClearAllPools();

            using var second = new SqliteBm25Index(path);
            await second.InitializeAsync(TestContext.Current.CancellationToken);
            var reopened = second.Add(MakeChunk("doc-b", 0, "after the restart"));

            Assert.DoesNotContain(reopened, firstIds);
            Assert.True(reopened > firstIds.Max(), $"allocator returned {reopened}, inside the restored range {firstIds.Min()}..{firstIds.Max()}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
