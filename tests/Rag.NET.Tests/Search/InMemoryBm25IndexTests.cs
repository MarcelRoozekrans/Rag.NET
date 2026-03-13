using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class InMemoryBm25IndexTests
{
    [Fact]
    public void Search_ReturnsEmpty_WhenIndexIsEmpty()
    {
        var index = new InMemoryBm25Index();
        var results = index.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Search_ReturnsMatchingDoc_WhenTermPresent()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "the quick brown fox", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "the lazy dog sleeps", DocumentId = "doc1", ChunkIndex = 1 });

        var results = index.Search("fox", topK: 5);

        Assert.Single(results);
        Assert.Equal(0, results[0].docId);
    }

    [Fact]
    public void Search_RanksHigherFrequencyTermHigher()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "cat cat cat", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "cat dog bird", DocumentId = "doc1", ChunkIndex = 1 });

        var results = index.Search("cat", topK: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].docId); // higher TF should rank first
    }

    [Fact]
    public void Search_RespectsTopK()
    {
        var index = new InMemoryBm25Index();
        for (int i = 0; i < 10; i++)
            index.Add(i, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = i });

        var results = index.Search("hello", topK: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Remove_DeletesAllChunksForDocument()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "hello universe", DocumentId = "doc2", ChunkIndex = 0 });

        index.Remove("doc1");

        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
        Assert.Equal(1, results[0].docId);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "Hello World", DocumentId = "doc1", ChunkIndex = 0 });

        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
    }

    [Fact]
    public void Search_IgnoresPunctuation()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "fox! jumps... over, the fence.", DocumentId = "doc1", ChunkIndex = 0 });

        var results = index.Search("jumps", topK: 5);
        Assert.Single(results);
    }
}
