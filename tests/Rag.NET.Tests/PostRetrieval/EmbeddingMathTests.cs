using Rag.NET.PostRetrieval;
using Xunit;

namespace Rag.NET.Tests.PostRetrieval;

public class EmbeddingMathTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new ReadOnlyMemory<float>([1f, 0f, 0f]);
        Assert.Equal(1f, EmbeddingMath.CosineSimilarity(a, a), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new ReadOnlyMemory<float>([1f, 0f]);
        var b = new ReadOnlyMemory<float>([0f, 1f]);
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new ReadOnlyMemory<float>([1f, 0f]);
        var b = new ReadOnlyMemory<float>([-1f, 0f]);
        Assert.Equal(-1f, EmbeddingMath.CosineSimilarity(a, b), precision: 5);
    }

    [Fact]
    public void CosineSimilarity_MismatchedLengths_ReturnsZero()
    {
        var a = new ReadOnlyMemory<float>([1f, 0f]);
        var b = new ReadOnlyMemory<float>([1f, 0f, 0f]);
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity(a, b));
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new ReadOnlyMemory<float>([0f, 0f, 0f]);
        var b = new ReadOnlyMemory<float>([1f, 2f, 3f]);
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity(a, b));
    }

    [Fact]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var a = new ReadOnlyMemory<float>([]);
        var b = new ReadOnlyMemory<float>([]);
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity(a, b));
    }
}
