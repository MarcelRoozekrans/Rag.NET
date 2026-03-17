using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class SqliteParentChunkStoreTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-parent-test-{Guid.NewGuid():N}.db");
    private SqliteParentChunkStore? _sut;

    private SqliteParentChunkStore CreateSut(string collection = "test-coll")
    {
        _sut = new SqliteParentChunkStore(_dbPath, collection);
        return _sut;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Add_ThenRestart_TryGetSucceeds()
    {
        var sut = CreateSut();
        sut.Add("doc-1", 0, "large parent text");
        await sut.DisposeAsync();

        _sut = new SqliteParentChunkStore(_dbPath, "test-coll");
        var found = _sut.TryGet("doc-1", 0, out var text);
        Assert.True(found);
        Assert.Equal("large parent text", text);
    }

    [Fact]
    public async Task Remove_ThenRestart_TryGetFails()
    {
        var sut = CreateSut();
        sut.Add("doc-1", 0, "large parent text");
        sut.Remove("doc-1");
        await sut.DisposeAsync();

        _sut = new SqliteParentChunkStore(_dbPath, "test-coll");
        var found = _sut.TryGet("doc-1", 0, out _);
        Assert.False(found);
    }

    [Fact]
    public async Task CollectionNameMismatch_WipesExistingData()
    {
        var sut = CreateSut("collection-A");
        sut.Add("doc-1", 0, "parent text");
        await sut.DisposeAsync();

        _sut = new SqliteParentChunkStore(_dbPath, "collection-B");
        var found = _sut.TryGet("doc-1", 0, out _);
        Assert.False(found);
    }

    [Fact]
    public void Add_MultipleParents_AllRetrievable()
    {
        var sut = CreateSut();
        sut.Add("doc-1", 0, "first parent");
        sut.Add("doc-1", 1, "second parent");

        Assert.True(sut.TryGet("doc-1", 0, out var t0));
        Assert.Equal("first parent", t0);
        Assert.True(sut.TryGet("doc-1", 1, out var t1));
        Assert.Equal("second parent", t1);
    }
}
