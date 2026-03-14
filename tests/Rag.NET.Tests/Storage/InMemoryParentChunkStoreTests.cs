using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class InMemoryParentChunkStoreTests
{
    private readonly InMemoryParentChunkStore _store = new();

    [Fact]
    public void Add_And_TryGet_ReturnsStoredText()
    {
        _store.Add("doc1", 0, "parent text");
        var found = _store.TryGet("doc1", 0, out var text);
        Assert.True(found);
        Assert.Equal("parent text", text);
    }

    [Fact]
    public void TryGet_NotFound_ReturnsFalse()
    {
        var found = _store.TryGet("missing", 0, out var text);
        Assert.False(found);
        Assert.Null(text);
    }

    [Fact]
    public void Remove_DeletesByDocumentId()
    {
        _store.Add("doc1", 0, "chunk 0");
        _store.Add("doc1", 1, "chunk 1");
        _store.Add("doc2", 0, "other doc");

        _store.Remove("doc1");

        Assert.False(_store.TryGet("doc1", 0, out _));
        Assert.False(_store.TryGet("doc1", 1, out _));
        Assert.True(_store.TryGet("doc2", 0, out _));
    }

    [Fact]
    public void GetParentKey_FormatsCorrectly()
    {
        var key = InMemoryParentChunkStore.GetParentKey("doc-123", 7);
        Assert.Equal("doc-123:7", key);
    }

    [Fact]
    public void FindParentIndex_ReturnsCorrectParent()
    {
        // Parents at positions 0-99, 100-199
        var parentBoundaries = new List<(int start, int end)> { (0, 99), (100, 199) };
        var idx = InMemoryParentChunkStore.FindParentIndex(parentBoundaries, childStart: 50);
        Assert.Equal(0, idx);

        idx = InMemoryParentChunkStore.FindParentIndex(parentBoundaries, childStart: 150);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void FindParentIndex_ChildOutsideAllBoundaries_ReturnsLastParent()
    {
        var parentBoundaries = new List<(int start, int end)> { (0, 99) };
        var idx = InMemoryParentChunkStore.FindParentIndex(parentBoundaries, childStart: 200);
        Assert.Equal(0, idx);
    }
}
