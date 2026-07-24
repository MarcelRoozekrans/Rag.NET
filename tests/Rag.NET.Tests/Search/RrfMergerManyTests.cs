using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class RrfMergerManyTests
{
    private static SearchResult MakeResult(string docId, int chunkIndex, double score = 1.0) =>
        new()
        {
            Chunk = new TextChunk
            {
                Text = $"{docId}-{chunkIndex}",
                DocumentId = new DocumentId(docId),
                ChunkIndex = chunkIndex,
            },
            Score = score,
        };

    private static (TextChunk chunk, double score) MakeBm25Hit(string docId, int chunkIndex, double score = 1.0) =>
        (new TextChunk { Text = $"{docId}-{chunkIndex}", DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex }, score);

    [Fact]
    public void MergeMany_ThreeLists_HandComputedWeightedRrf()
    {
        // k = 60, 1-based ranks:
        //   dense  (w 0.5): [A, B]      → A: 0.5/61,             B: 0.5/62
        //   bm25   (w 0.3): [B, C]      → B: 0.3/61,             C: 0.3/62
        //   sparse (w 0.2): [A, C, B]   → A: 0.2/61, C: 0.2/62,  B: 0.2/63
        // A = 0.7/61 ≈ 0.0114754 ; B = 0.5/62 + 0.3/61 + 0.2/63 ≈ 0.0161571 ; C = 0.5/62 ≈ 0.0080645
        var dense = new[] { MakeResult("A", 0), MakeResult("B", 0) };
        var bm25 = new[] { MakeResult("B", 0), MakeResult("C", 0) };
        var sparse = new[] { MakeResult("A", 0), MakeResult("C", 0), MakeResult("B", 0) };

        var merged = RrfMerger.MergeMany(
            [(dense, 0.5), (bm25, 0.3), (sparse, 0.2)], topK: 10, k: 60);

        Assert.Equal(3, merged.Count);
        Assert.Equal("B", merged[0].Chunk.DocumentId.Value);
        Assert.Equal((0.5 / 62) + (0.3 / 61) + (0.2 / 63), merged[0].Score, precision: 10);
        Assert.Equal("A", merged[1].Chunk.DocumentId.Value);
        Assert.Equal(0.7 / 61, merged[1].Score, precision: 10);
        Assert.Equal("C", merged[2].Chunk.DocumentId.Value);
        Assert.Equal(0.5 / 62, merged[2].Score, precision: 10);
    }

    [Fact]
    public void MergeMany_TopKApplied_AndNonPositiveTopKEmpty()
    {
        var dense = new[] { MakeResult("A", 0), MakeResult("B", 0), MakeResult("C", 0) };

        var top2 = RrfMerger.MergeMany([(dense, 1.0)], topK: 2, k: 60);
        Assert.Equal(2, top2.Count);

        Assert.Empty(RrfMerger.MergeMany([(dense, 1.0)], topK: 0, k: 60));
    }

    [Fact]
    public void Merge_TwoArmOverloadWithOptions_DelegatesToMergeMany_Equivalent()
    {
        var dense = new[] { MakeResult("A", 0), MakeResult("B", 1), MakeResult("C", 0) };
        var bm25Hits = new[] { MakeBm25Hit("B", 1), MakeBm25Hit("D", 0) };
        var options = new EnsembleOptions { DenseWeight = 0.7f, Bm25Weight = 0.3f, K = 45 };

        var viaOldOverload = RrfMerger.Merge(dense, bm25Hits, topK: 10, options);
        var viaMergeMany = RrfMerger.MergeMany(
            [(dense, options.DenseWeight), (RrfMerger.ToSearchResults(bm25Hits), options.Bm25Weight)],
            topK: 10, k: options.K);

        Assert.Equal(viaMergeMany.Count, viaOldOverload.Count);
        for (var i = 0; i < viaOldOverload.Count; i++)
        {
            Assert.Equal(viaMergeMany[i].Chunk.DocumentId.Value, viaOldOverload[i].Chunk.DocumentId.Value);
            Assert.Equal(viaMergeMany[i].Chunk.ChunkIndex, viaOldOverload[i].Chunk.ChunkIndex);
            Assert.Equal(viaMergeMany[i].Score, viaOldOverload[i].Score, precision: 12);
        }
    }

    [Fact]
    public void Merge_UnweightedOverload_DelegatesToMergeMany_Equivalent()
    {
        var dense = new[] { MakeResult("A", 0), MakeResult("B", 0) };
        var bm25Hits = new[] { MakeBm25Hit("B", 0), MakeBm25Hit("C", 0) };

        var viaOldOverload = RrfMerger.Merge(dense, bm25Hits, topK: 10);
        var viaMergeMany = RrfMerger.MergeMany(
            [(dense, 1.0), (RrfMerger.ToSearchResults(bm25Hits), 1.0)], topK: 10, k: 60);

        Assert.Equal(viaMergeMany.Count, viaOldOverload.Count);
        for (var i = 0; i < viaOldOverload.Count; i++)
        {
            Assert.Equal(viaMergeMany[i].Chunk.DocumentId.Value, viaOldOverload[i].Chunk.DocumentId.Value);
            Assert.Equal(viaMergeMany[i].Score, viaOldOverload[i].Score, precision: 12);
        }
    }

    [Fact]
    public void MergeMany_SharedChunk_ChunkInstanceFromFirstListContainingIt()
    {
        var denseChunk = MakeResult("A", 0);
        var sparseChunk = MakeResult("A", 0);

        var merged = RrfMerger.MergeMany([(new[] { denseChunk }, 0.5), (new[] { sparseChunk }, 0.5)], topK: 5, k: 60);

        Assert.Single(merged);
        Assert.Same(denseChunk.Chunk, merged[0].Chunk);
    }
}
