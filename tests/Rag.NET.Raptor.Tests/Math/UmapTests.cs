using Rag.NET.Raptor.Math;
using Xunit;

namespace Rag.NET.Raptor.Tests.Math;

public class UmapTests
{
    [Fact]
    public void Fit_ReducesDimensionality()
    {
        var data = CreateRandomData(10, 50, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 3);
        Assert.Equal(10, result.Length);
        Assert.Equal(3, result[0].Length);
    }

    [Fact]
    public void Fit_PreservesRelativeDistances()
    {
        var cluster1 = CreateCluster(center: 0f, count: 5, dims: 50, seed: 1);
        var cluster2 = CreateCluster(center: 10f, count: 5, dims: 50, seed: 2);
        var data = cluster1.Concat(cluster2).ToArray();
        var result = Umap.Fit(data, targetDimensions: 3);
        var intra1 = EuclideanDistance(result[0], result[1]);
        var inter = EuclideanDistance(result[0], result[5]);
        Assert.True(intra1 < inter, "Intra-cluster distance should be less than inter-cluster distance");
    }

    [Fact]
    public void Fit_WithFewerPointsThanDimensions_DoesNotThrow()
    {
        var data = CreateRandomData(3, 50, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 2);
        Assert.Equal(3, result.Length);
        Assert.Equal(2, result[0].Length);
    }

    [Fact]
    public void Fit_TargetDimensionsEqualToInput_ReturnsOriginalShape()
    {
        var data = CreateRandomData(5, 3, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 3);
        Assert.Equal(5, result.Length);
        Assert.Equal(3, result[0].Length);
    }

    [Fact]
    public void Fit_EmptyData_ReturnsEmptyArray()
    {
        var result = Umap.Fit([], targetDimensions: 2);
        Assert.Empty(result);
    }

    [Fact]
    public void Fit_SinglePoint_ReturnsResult()
    {
        var data = new[] { new[] { 1f, 2f, 3f } };
        var result = Umap.Fit(data, targetDimensions: 2);
        Assert.Single(result);
        Assert.Equal(2, result[0].Length);
    }

    [Fact]
    public void Fit_NullData_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Umap.Fit(null!, 2));
    }

    [Fact]
    public void Fit_TargetDimensionsGreaterThanInput_PadsWithZeros()
    {
        var data = CreateRandomData(5, 3, seed: 42);
        var result = Umap.Fit(data, targetDimensions: 5);
        Assert.Equal(5, result.Length);
        Assert.Equal(5, result[0].Length);
    }

    private static float[][] CreateRandomData(int count, int dims, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(0, dims).Select(_ => (float)rng.NextDouble()).ToArray())
            .ToArray();
    }

    private static float[][] CreateCluster(float center, int count, int dims, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(0, dims).Select(_ => center + (float)(rng.NextDouble() * 0.1)).ToArray())
            .ToArray();
    }

    private static double EuclideanDistance(float[] a, float[] b)
        => System.Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
}
