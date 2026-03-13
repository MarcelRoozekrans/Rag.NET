using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class RrfMergerTests
{
    private static TextChunk Chunk(string docId, int chunkIndex, string text = "text") =>
        new() { DocumentId = docId, ChunkIndex = chunkIndex, Text = text };

    private static SearchResult Result(TextChunk chunk, double score = 1.0) =>
        new() { Chunk = chunk, Score = score };

    [Fact]
    public void Merge_ReturnsEmpty_WhenBothListsEmpty()
    {
        var result = RrfMerger.Merge([], [], [], topK: 5);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_ReturnsEmpty_WhenTopKIsZero()
    {
        var chunk = Chunk("doc", 0);
        var result = RrfMerger.Merge([Result(chunk)], [], [chunk], topK: 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_RespectsTopK()
    {
        var chunks = Enumerable.Range(0, 10).Select(i => Chunk("doc", i)).ToList();
        var dense = chunks.Select(c => Result(c, 1.0)).ToList();

        var result = RrfMerger.Merge(dense, [], chunks, topK: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_DeduplicatesChunkInBothLists()
    {
        var chunk = Chunk("doc", 0);
        var dense = new List<SearchResult> { Result(chunk, 0.9) };
        var bm25 = new List<(int docId, double score)> { (0, 50.0) };

        var result = RrfMerger.Merge(dense, bm25, [chunk], topK: 5);

        Assert.Single(result);
    }

    [Fact]
    public void Merge_ChunkInBothLists_ScoresHigherThanChunkInOneList()
    {
        // chunk0 in both, chunk1 only in dense, chunk2 only in BM25
        var chunk0 = Chunk("doc", 0, "shared");
        var chunk1 = Chunk("doc", 1, "dense only");
        var chunk2 = Chunk("doc", 2, "bm25 only");

        var allChunks = new List<TextChunk> { chunk0, chunk1, chunk2 };
        var dense = new List<SearchResult> { Result(chunk0, 0.9), Result(chunk1, 0.8) };
        var bm25 = new List<(int docId, double score)> { (0, 50.0), (2, 40.0) };

        var result = RrfMerger.Merge(dense, bm25, allChunks, topK: 5);

        Assert.Equal(3, result.Count);
        // chunk0 (rank 1 in both) has highest RRF score
        Assert.Equal(0, result[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Merge_WorksWithOnlyDenseResults()
    {
        var chunks = new[] { Chunk("doc", 0), Chunk("doc", 1) };
        var dense = chunks.Select(c => Result(c)).ToList();

        var result = RrfMerger.Merge(dense, [], [], topK: 5);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_WorksWithOnlyBm25Results()
    {
        var chunk = Chunk("doc", 0);
        var bm25 = new List<(int docId, double score)> { (0, 42.0) };

        var result = RrfMerger.Merge([], bm25, [chunk], topK: 5);

        Assert.Single(result);
        Assert.Equal(0, result[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Merge_RrfScore_IsCorrect()
    {
        // Single chunk, rank 1 in dense only → score = 1/(60+1) = 1/61
        var chunk = Chunk("doc", 0);
        var result = RrfMerger.Merge([Result(chunk)], [], [chunk], topK: 5);

        Assert.Single(result);
        var expected = 1.0 / 61.0;
        Assert.Equal(expected, result[0].Score, precision: 10);
    }
}
