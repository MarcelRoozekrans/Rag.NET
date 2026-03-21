using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class RrfMergerWeightedTests
{
    private static SearchResult MakeDenseResult(string docId, int chunkIndex, double score) =>
        new()
        {
            Chunk = new TextChunk { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = $"{docId}-{chunkIndex}" },
            Score = score
        };

    private static (TextChunk chunk, double score) MakeBm25Hit(string docId, int chunkIndex) =>
        (new TextChunk { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = $"{docId}-{chunkIndex}" }, 1.0);

    [Fact]
    public void Merge_Weighted_EqualWeights_BothResultsReturned()
    {
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.5f, Bm25Weight = 0.5f, K = 60 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Merge_Weighted_Bm25Heavy_RanksBm25ResultHigher()
    {
        // doc-A rank 0 dense only, doc-B rank 0 BM25 only
        // DenseWeight=0.1, Bm25Weight=0.9 → doc-B score = 0.9/61 > doc-A score = 0.1/61
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.1f, Bm25Weight = 0.9f, K = 60 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal("doc-B", results[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public void Merge_Weighted_DenseHeavy_RanksDenseResultHigher()
    {
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.9f, Bm25Weight = 0.1f, K = 60 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal("doc-A", results[0].Chunk.DocumentId.ToString());
    }

    [Fact]
    public void Merge_Weighted_KZero_ClampedToOne_DoesNotThrow()
    {
        // K < 1 should be clamped to 1 — result should not throw
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = new[] { MakeBm25Hit("doc-B", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.5f, Bm25Weight = 0.5f, K = 0 };

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Merge_Weighted_EmptyBm25_ReturnsOnlyDenseResults()
    {
        var dense   = new[] { MakeDenseResult("doc-A", 0, 0.9) };
        var bm25    = Array.Empty<(TextChunk, double)>();
        var options = new EnsembleOptions();

        var results = RrfMerger.Merge(dense, bm25, topK: 2, options);

        Assert.Single(results);
        Assert.Equal("doc-A", results[0].Chunk.DocumentId.ToString());
    }
}
