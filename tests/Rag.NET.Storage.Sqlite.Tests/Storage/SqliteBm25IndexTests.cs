using System.Globalization;
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
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
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
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
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
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
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
        sut.Add(1, MakeChunk("doc-1", 0, "the quick brown fox"));
        sut.Add(2, MakeChunk("doc-2", 0, "the lazy dog"));

        var results = sut.Search("fox", topK: 5);
        Assert.Single(results); // only first chunk matches "fox"
        Assert.Equal("doc-1", results[0].chunk.DocumentId);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllChunks()
    {
        var sut = CreateSut();
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
        sut.Add(2, MakeChunk("doc-2", 0, "foo bar"));

        await sut.ClearAsync(TestContext.Current.CancellationToken);

        var results = sut.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ClearAsync_ThenRestart_SearchFindsNothing()
    {
        var sut = CreateSut();
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
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
        sut.Add(1, MakeChunk("doc-1", 0, "hello world"));
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
            sut.Add(1, MakeChunk("doc-1", 0, "hello")));
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

    [Fact]
    public void Search_WithMetadataFilter_ExcludesNonMatchingChunks()
    {
        using var sut = CreateSut();
        sut.Add(1, FilterChunk(1, "shared search term", "a"));
        sut.Add(2, FilterChunk(2, "shared search term", "b"));

        var results = sut.Search(
            "shared search term",
            topK: 10,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }
}
