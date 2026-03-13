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
        Assert.Equal("doc1", results[0].chunk.DocumentId);
        Assert.Equal(0, results[0].chunk.ChunkIndex);
    }

    [Fact]
    public void Search_RanksHigherFrequencyTermHigher()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "cat cat cat", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "cat dog bird", DocumentId = "doc1", ChunkIndex = 1 });

        var results = index.Search("cat", topK: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].chunk.ChunkIndex); // higher TF should rank first
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
        Assert.Equal("doc2", results[0].chunk.DocumentId);
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

    [Fact]
    public void Search_ReturnsEmpty_WhenTopKIsZero()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = 0 });
        var results = index.Search("hello", topK: 0);
        Assert.Empty(results);
    }

    [Fact]
    public void Remove_OnNonExistentDocument_IsNoOp()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = 0 });
        index.Remove("does-not-exist"); // should not throw
        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
    }

    [Fact]
    public void Search_MultiWordQuery_AccumulatesScoresAcrossTerms()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "quick brown fox", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "quick lazy dog", DocumentId = "doc2", ChunkIndex = 0 });

        // "doc1" (quick brown fox) has both "quick" and "fox"; "doc2" only has "quick"
        var results = index.Search("quick fox", topK: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc1", results[0].chunk.DocumentId); // "doc1" ranks higher — matches both terms
    }

    // Gap 1 — empty/whitespace query returns empty results
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_EmptyOrWhitespaceQuery_ReturnsEmpty(string query)
    {
        var index = new InMemoryBm25Index();
        index.Add(0, new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "quick brown fox", DocumentId = "doc1", ChunkIndex = 1 });

        var results = index.Search(query, topK: 5);

        Assert.Empty(results);
    }

    // Gap 2 — duplicate docId is idempotent
    [Fact]
    public void Add_DuplicateDocId_IsIdempotent()
    {
        var index = new InMemoryBm25Index();
        var chunk = new TextChunk { Text = "hello world", DocumentId = "doc1", ChunkIndex = 0 };

        // Add the same docId twice
        index.Add(42, chunk);
        index.Add(42, chunk);

        // Add another doc so we can compare ranking — it should not be demoted by double-counting
        index.Add(99, new TextChunk { Text = "hello universe", DocumentId = "doc2", ChunkIndex = 0 });

        var results = index.Search("hello", topK: 5);

        // The duplicated doc should appear exactly once
        Assert.Equal(2, results.Count);
        Assert.Single(results, r => string.Equals(r.chunk.DocumentId, "doc1", StringComparison.Ordinal));
        Assert.Single(results, r => string.Equals(r.chunk.DocumentId, "doc2", StringComparison.Ordinal));
    }

    // Gap 3 — IDF boundary when df == N: score must still be positive
    [Fact]
    public void Search_AllDocsContainTerm_ScoreIsPositive()
    {
        var index = new InMemoryBm25Index();
        // All 3 documents contain "common" → df == N == 3
        index.Add(0, new TextChunk { Text = "common ground", DocumentId = "doc1", ChunkIndex = 0 });
        index.Add(1, new TextChunk { Text = "common sense", DocumentId = "doc2", ChunkIndex = 0 });
        index.Add(2, new TextChunk { Text = "common people", DocumentId = "doc3", ChunkIndex = 0 });

        var results = index.Search("common", topK: 5);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.score > 0, "Score must be positive even when df == N"));
    }
}
