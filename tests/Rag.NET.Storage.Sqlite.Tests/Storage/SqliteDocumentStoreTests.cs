using Microsoft.Data.Sqlite;
using Rag.NET.Models;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public sealed class SqliteDocumentStoreTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-dm-{Guid.NewGuid():N}.db");
    private SqliteDocumentStore? _sut;

    private SqliteDocumentStore CreateSut(string collection = "test-coll")
    {
        _sut = new SqliteDocumentStore(_dbPath, collection);
        return _sut;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static DocumentMetadata MakeMetadata(string docId, string fileName = "test.txt", string? contentType = "text/plain")
        => new() { DocumentId = new DocumentId(docId), FileName = fileName, ContentType = contentType };

    private static TextChunk MakeChunk(string docId, int idx, string text, int start = 0, int end = 0)
        => new() { DocumentId = new DocumentId(docId), ChunkIndex = idx, Text = text, StartPosition = start, EndPosition = end };

    [Fact]
    public async Task Add_ThenGetDocuments_ReturnsSummaryWithCorrectFields()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        var metadata = MakeMetadata("doc-1", "report.pdf", "application/pdf");

        sut.Add(metadata, [MakeChunk("doc-1", 0, "hello"), MakeChunk("doc-1", 1, "world")]);

        var docs = await sut.GetDocumentsAsync(ct);
        Assert.Single(docs);
        Assert.Equal(new DocumentId("doc-1"), docs[0].DocumentId);
        Assert.Equal("report.pdf",      docs[0].FileName);
        Assert.Equal("application/pdf", docs[0].ContentType);
        Assert.Equal(2,                 docs[0].ChunkCount);
        Assert.True(docs[0].IngestedAt <= DateTimeOffset.UtcNow);
        Assert.True(docs[0].IngestedAt > DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public async Task Add_ThenGetChunks_ReturnsOriginalTextChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        var chunk0 = MakeChunk("doc-1", 0, "hello world", start: 0, end: 11);
        var chunk1 = MakeChunk("doc-1", 1, "foo bar", start: 12, end: 19);

        sut.Add(MakeMetadata("doc-1"), [chunk0, chunk1]);

        var chunks = await sut.GetChunksAsync("doc-1", ct);
        Assert.Equal(2,             chunks.Count);
        Assert.Equal("hello world", chunks[0].Text);
        Assert.Equal(0,             chunks[0].StartPosition);
        Assert.Equal(11,            chunks[0].EndPosition);
        Assert.Equal("foo bar",     chunks[1].Text);
        Assert.Equal(1,             chunks[1].ChunkIndex);
        Assert.Equal(12,            chunks[1].StartPosition);
    }

    [Fact]
    public async Task Add_ThenGetStats_ReturnsCorrectCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "a"), MakeChunk("doc-1", 1, "b")]);
        sut.Add(MakeMetadata("doc-2"), [MakeChunk("doc-2", 0, "c")]);

        var stats = await sut.GetStatsAsync(ct);
        Assert.Equal(2, stats.DocumentCount);
        Assert.Equal(3, stats.TotalChunkCount);
    }

    [Fact]
    public async Task Remove_ThenGetDocuments_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        sut.Remove("doc-1");

        var docs = await sut.GetDocumentsAsync(ct);
        Assert.Empty(docs);
    }

    [Fact]
    public async Task Remove_ThenGetChunks_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        sut.Remove("doc-1");

        var chunks = await sut.GetChunksAsync("doc-1", ct);
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllDocumentsAndChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        sut.Add(MakeMetadata("doc-2"), [MakeChunk("doc-2", 0, "world")]);

        await sut.ClearAsync(ct);

        var docs  = await sut.GetDocumentsAsync(ct);
        var stats = await sut.GetStatsAsync(ct);
        Assert.Empty(docs);
        Assert.Equal(0, stats.TotalChunkCount);
    }

    [Fact]
    public async Task Add_ThenRestart_GetDocumentsFindsDocument()
    {
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-1", "report.pdf"), [MakeChunk("doc-1", 0, "hello")]);
        await sut.DisposeAsync();

        _sut = new SqliteDocumentStore(_dbPath, "test-coll");
        var docs = await _sut.GetDocumentsAsync(TestContext.Current.CancellationToken);
        Assert.Single(docs);
        Assert.Equal(new DocumentId("doc-1"), docs[0].DocumentId);
    }

    [Fact]
    public async Task CollectionNameMismatch_WipesExistingData()
    {
        var sut = CreateSut("collection-A");
        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        await sut.DisposeAsync();

        _sut = new SqliteDocumentStore(_dbPath, "collection-B");
        var docs = await _sut.GetDocumentsAsync(TestContext.Current.CancellationToken);
        Assert.Empty(docs);
    }

    [Fact]
    public async Task InitializeAsync_CanBeAwaited_ThenAddWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        await sut.InitializeAsync(ct);

        sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]);
        var docs = await sut.GetDocumentsAsync(ct);
        Assert.Single(docs);
    }

    [Fact]
    public void Add_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            sut.Add(MakeMetadata("doc-1"), [MakeChunk("doc-1", 0, "hello")]));
    }

    [Fact]
    public async Task GetDocuments_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = CreateSut();
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.GetDocumentsAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A <c>tags_json</c> column that will not deserialize is backend corruption, and
    /// <see cref="SqliteDocumentStore"/> throws naming the document rather than silently listing
    /// it with empty tags (#521) — the same posture <c>metadata_json</c> gets, applied to the
    /// second data type that goes through <c>MetadataSerializer</c>. Written directly through a
    /// raw connection: nothing reachable through the public API can produce malformed JSON here.
    /// </summary>
    [Fact]
    public async Task ACorruptTagsJsonColumnThrowsNamingTheDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-corrupt-tags"), [MakeChunk("doc-corrupt-tags", 0, "hello")]);

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE rag_documents SET tags_json = '{not json' WHERE doc_id = 'doc-corrupt-tags'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetDocumentsAsync(ct));

        Assert.Contains("doc-corrupt-tags", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>metadata_json</c> column that will not deserialize is backend corruption, and
    /// <see cref="SqliteDocumentStore"/> throws naming the document and chunk rather than
    /// silently returning the chunk with empty metadata (#521).
    /// </summary>
    [Fact]
    public async Task ACorruptMetadataJsonColumnThrowsNamingTheChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();
        sut.Add(MakeMetadata("doc-corrupt-chunk"), [MakeChunk("doc-corrupt-chunk", 5, "hello")]);

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE rag_chunks SET metadata_json = '{not json' " +
                "WHERE doc_id = 'doc-corrupt-chunk' AND chunk_index = 5";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        SqliteConnection.ClearAllPools();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetChunksAsync("doc-corrupt-chunk", ct));

        Assert.Contains("doc-corrupt-chunk", error.Message, StringComparison.Ordinal);
        Assert.Contains("5", error.Message, StringComparison.Ordinal);
    }
}
