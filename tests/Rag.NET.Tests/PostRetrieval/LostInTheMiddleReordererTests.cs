using Rag.NET.Models;
using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class LostInTheMiddleReordererTests
{
    private static SearchResult MakeResult(double score) => new()
    {
        Chunk = new TextChunk { Text = $"text-{score}", DocumentId = "doc-1", ChunkIndex = 0 },
        Score = score,
    };

    [Fact]
    public void Reorder_EmptyList_ReturnsEmpty()
    {
        var result = LostInTheMiddleReorderer.Reorder([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Reorder_SingleItem_ReturnsSame()
    {
        var items = new[] { MakeResult(1.0) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Single(result);
        Assert.Equal(1.0, result[0].Score);
    }

    [Fact]
    public void Reorder_TwoItems_ReturnsBestFirstSecondBestLast()
    {
        var items = new[] { MakeResult(0.9), MakeResult(0.8) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.8, result[1].Score);
    }

    [Fact]
    public void Reorder_ThreeItems_PlacesBestFirstSecondBestLast()
    {
        // Input: [0.9, 0.8, 0.7] → Output: [0.9, 0.7, 0.8]
        var items = new[] { MakeResult(0.9), MakeResult(0.8), MakeResult(0.7) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.7, result[1].Score);
        Assert.Equal(0.8, result[2].Score);
    }

    [Fact]
    public void Reorder_FourItems_FillsOutsideIn()
    {
        // Input: [0.9, 0.8, 0.7, 0.6] → Output: [0.9, 0.7, 0.6, 0.8]
        var items = new[] { MakeResult(0.9), MakeResult(0.8), MakeResult(0.7), MakeResult(0.6) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.7, result[1].Score);
        Assert.Equal(0.6, result[2].Score);
        Assert.Equal(0.8, result[3].Score);
    }

    [Fact]
    public void Reorder_FiveItems_FillsOutsideIn()
    {
        // Input: [0.9, 0.8, 0.7, 0.6, 0.5] → Output: [0.9, 0.7, 0.5, 0.6, 0.8]
        var items = new[] { MakeResult(0.9), MakeResult(0.8), MakeResult(0.7), MakeResult(0.6), MakeResult(0.5) };
        var result = LostInTheMiddleReorderer.Reorder(items);
        Assert.Equal(0.9, result[0].Score);
        Assert.Equal(0.7, result[1].Score);
        Assert.Equal(0.5, result[2].Score);
        Assert.Equal(0.6, result[3].Score);
        Assert.Equal(0.8, result[4].Score);
    }
}
