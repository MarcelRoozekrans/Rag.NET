using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class RrfMergerTests
{
    private static TextChunk Chunk(string docId, int chunkIndex, string text = "text") =>
        new() { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = text };

    private static SearchResult Result(TextChunk chunk, double score = 1.0) =>
        new() { Chunk = chunk, Score = score };

    [Fact]
    public void Merge_ReturnsEmpty_WhenBothListsEmpty()
    {
        var result = RrfMerger.Merge([], [], topK: 5);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_ReturnsEmpty_WhenTopKIsZero()
    {
        var chunk = Chunk("doc", 0);
        var result = RrfMerger.Merge([Result(chunk)], [], topK: 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_RespectsTopK()
    {
        var chunks = Enumerable.Range(0, 10).Select(i => Chunk("doc", i)).ToList();
        var dense = chunks.Select(c => Result(c, 1.0)).ToList();

        var result = RrfMerger.Merge(dense, [], topK: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_DeduplicatesChunkInBothLists()
    {
        var chunk = Chunk("doc", 0);
        var dense = new List<SearchResult> { Result(chunk, 0.9) };
        var bm25 = new List<(TextChunk chunk, double score)> { (chunk, 50.0) };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        Assert.Single(result);
    }

    [Fact]
    public void Merge_ChunkInBothLists_ScoresHigherThanChunkInOneList()
    {
        // chunk0 in both, chunk1 only in dense, chunk2 only in BM25
        var chunk0 = Chunk("doc", 0, "shared");
        var chunk1 = Chunk("doc", 1, "dense only");
        var chunk2 = Chunk("doc", 2, "bm25 only");

        var dense = new List<SearchResult> { Result(chunk0, 0.9), Result(chunk1, 0.8) };
        var bm25 = new List<(TextChunk chunk, double score)> { (chunk0, 50.0), (chunk2, 40.0) };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        Assert.Equal(3, result.Count);
        // chunk0 (rank 1 in both) has highest RRF score
        Assert.Equal(0, result[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Merge_WorksWithOnlyDenseResults()
    {
        var chunks = new[] { Chunk("doc", 0), Chunk("doc", 1) };
        var dense = chunks.Select(c => Result(c)).ToList();

        var result = RrfMerger.Merge(dense, [], topK: 5);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_WorksWithOnlyBm25Results()
    {
        var chunk = Chunk("doc", 0);
        var bm25 = new List<(TextChunk chunk, double score)> { (chunk, 42.0) };

        var result = RrfMerger.Merge([], bm25, topK: 5);

        Assert.Single(result);
        Assert.Equal(0, result[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Merge_RrfScore_IsCorrect()
    {
        // Single chunk, rank 1 in dense only → score = 1/(60+1) = 1/61
        var chunk = Chunk("doc", 0);
        var result = RrfMerger.Merge([Result(chunk)], [], topK: 5);

        Assert.Single(result);
        var expected = 1.0 / 61.0;
        Assert.Equal(expected, result[0].Score, precision: 10);
    }

    [Fact]
    public void Merge_ReturnsEmpty_WhenTopKIsNegative()
    {
        var chunk = Chunk("doc", 0);
        var result = RrfMerger.Merge([Result(chunk)], [], topK: -1);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_RrfScore_SumsContributionsFromBothLists()
    {
        // chunk0 rank 1 in dense AND rank 1 in BM25 → score = 1/61 + 1/61 = 2/61
        var chunk = Chunk("doc", 0);
        var dense = new List<SearchResult> { Result(chunk, 0.9) };
        var bm25 = new List<(TextChunk chunk, double score)> { (chunk, 50.0) };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        Assert.Single(result);
        var expected = 1.0 / 61.0 + 1.0 / 61.0;
        Assert.Equal(expected, result[0].Score, precision: 10);
    }

    [Fact]
    public void Merge_SortOrderIsDescendingByRrfScore()
    {
        // chunk0 rank 1 in both lists → 2/61; chunk1 rank 2 dense only → 1/62; chunk2 rank 2 bm25 only → 1/62
        // chunk1 and chunk2 are tied for second place
        var chunk0 = Chunk("doc", 0, "shared top");
        var chunk1 = Chunk("doc", 1, "dense second");
        var chunk2 = Chunk("doc", 2, "bm25 second");
        var dense = new List<SearchResult> { Result(chunk0, 0.9), Result(chunk1, 0.8) };
        var bm25 = new List<(TextChunk chunk, double score)> { (chunk0, 50.0), (chunk2, 40.0) };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        Assert.Equal(3, result.Count);
        // Scores must be non-ascending
        Assert.True(result[0].Score > result[1].Score);
        Assert.True(result[1].Score >= result[2].Score);
        // chunk0 (rank 1 in both) must be first
        Assert.Equal(0, result[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Merge_TopKLargerThanAvailableChunks_ReturnsAllChunks()
    {
        var chunks = new[] { Chunk("doc", 0), Chunk("doc", 1) };
        var dense = chunks.Select(c => Result(c)).ToList();

        var result = RrfMerger.Merge(dense, [], topK: 100);

        Assert.Equal(2, result.Count); // capped at available, not padded
    }

    // Gap 4 — very unbalanced lists: 100 dense + 1 BM25
    [Fact]
    public void Merge_VeryUnbalancedLists_Bm25TopResultAppears()
    {
        // 100 dense results
        var denseChunks = new List<SearchResult>(100);
        for (int i = 0; i < 100; i++)
            denseChunks.Add(Result(Chunk("dense", i), score: 1.0 - i * 0.001));

        // 1 BM25 result that is NOT in the dense list
        var bm25Chunk = Chunk("bm25", 0, "unique bm25 content");
        var bm25 = new List<(TextChunk chunk, double score)> { (bm25Chunk, 42.0) };

        var result = RrfMerger.Merge(denseChunks, bm25, topK: 5);

        // TopK must be respected
        Assert.Equal(5, result.Count);

        // The BM25 chunk (rank 1 in its list → score = 1/61) should appear in the merged output
        var bm25Result = result.FirstOrDefault(r =>
            string.Equals(r.Chunk.DocumentId, "bm25", StringComparison.Ordinal));
        Assert.NotNull(bm25Result);
        Assert.Equal(1.0 / 61.0, bm25Result!.Score, precision: 10);
    }
}
