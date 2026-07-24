using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public sealed class SqliteEmbeddingVersionStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-embver-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task SetAsync_ThenGetAll_ReturnsStoredStamp()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        await sut.SetAsync("doc-1", "openai/text-embedding-3-small", 1536, TestContext.Current.CancellationToken);

        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(all);
        Assert.Equal("doc-1", row.DocumentId);
        Assert.Equal("openai/text-embedding-3-small", row.ModelId);
        Assert.Equal(1536, row.Dimension);
    }

    [Fact]
    public async Task SetAsync_SameDocument_ReplacesExistingRow()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        await sut.SetAsync("doc-1", "model-v1", 384, TestContext.Current.CancellationToken);
        await sut.SetAsync("doc-1", "model-v2", 768, TestContext.Current.CancellationToken);

        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(all);
        Assert.Equal("model-v2", row.ModelId);
        Assert.Equal(768, row.Dimension);
    }

    [Fact]
    public async Task GetAllAsync_EmptyStore_ReturnsEmpty()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(all);
    }

    [Fact]
    public async Task GetAllAsync_MultipleDocuments_ReturnsAll()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        await sut.SetAsync("doc-a", "model-1", 128, TestContext.Current.CancellationToken);
        await sut.SetAsync("doc-b", "model-1", 128, TestContext.Current.CancellationToken);
        await sut.SetAsync("doc-c", "model-2", 256, TestContext.Current.CancellationToken);

        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, all.Count);
        Assert.Contains(("doc-a", "model-1", 128), all);
        Assert.Contains(("doc-b", "model-1", 128), all);
        Assert.Contains(("doc-c", "model-2", 256), all);
    }

    [Fact]
    public async Task RemoveAsync_StampGone_GetAllOmitsIt()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        await sut.SetAsync("doc-1", "model-1", 128, TestContext.Current.CancellationToken);
        await sut.RemoveAsync("doc-1", TestContext.Current.CancellationToken);

        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(all);
    }

    [Fact]
    public async Task RemoveAsync_UnknownDocument_DoesNotThrow()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        var ex = await Record.ExceptionAsync(() => sut.RemoveAsync("missing", TestContext.Current.CancellationToken));
        Assert.Null(ex);
    }

    [Fact]
    public async Task SurvivesRestart_DataPersistedToSqlite()
    {
        var sut1 = new SqliteEmbeddingVersionStore(_dbPath);
        await sut1.SetAsync("doc-1", "model-1", 1024, TestContext.Current.CancellationToken);

        // Simulate restart — new instance, same db file
        var sut2 = new SqliteEmbeddingVersionStore(_dbPath);
        var all = await sut2.GetAllAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(all);
        Assert.Equal(("doc-1", "model-1", 1024), row);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var sut = new SqliteEmbeddingVersionStore(_dbPath);
        await sut.SetAsync("doc-1", "model-1", 128, TestContext.Current.CancellationToken);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        var all = await sut.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all);
    }
}
