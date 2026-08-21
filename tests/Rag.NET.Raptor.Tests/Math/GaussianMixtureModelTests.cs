using System.Diagnostics.CodeAnalysis;
using Rag.NET.Raptor.Math;
using Xunit;

namespace Rag.NET.Raptor.Tests.Math;

[SuppressMessage("Performance", "HLQ013:Use foreach loop", Justification = "Index-based access required to populate jagged test fixtures")]
public class GaussianMixtureModelTests
{
    [Fact]
    public void Fit_TwoClusters_AssignsPointsCorrectly()
    {
        var cluster1 = CreateCluster(center: [0f, 0f], count: 20, spread: 0.1f, seed: 1);
        var cluster2 = CreateCluster(center: [10f, 10f], count: 20, spread: 0.1f, seed: 2);
        var data = cluster1.Concat(cluster2).ToArray();

        var result = GaussianMixtureModel.Fit(data, k: 2);

        Assert.Equal(40, result.Assignments.Length);
        var label1 = result.Assignments[0];
        Assert.All(result.Assignments.Take(20), a => Assert.Equal(label1, a));
        var label2 = result.Assignments[20];
        Assert.NotEqual(label1, label2);
        Assert.All(result.Assignments.Skip(20), a => Assert.Equal(label2, a));
    }

    [Fact]
    public void Fit_ReturnsResponsibilitiesWithSoftAssignment()
    {
        var cluster1 = CreateCluster(center: [0f, 0f], count: 10, spread: 0.1f, seed: 1);
        var cluster2 = CreateCluster(center: [10f, 10f], count: 10, spread: 0.1f, seed: 2);
        var data = cluster1.Concat(cluster2).ToArray();

        var result = GaussianMixtureModel.Fit(data, k: 2);

        Assert.Equal(20, result.Responsibilities.Length);
        Assert.Equal(2, result.Responsibilities[0].Length);
        foreach (var row in result.Responsibilities)
        {
            double sum = 0;
            foreach (float v in row)
            {
                sum += v;
            }

            Assert.InRange(sum, 0.99, 1.01);
        }
    }

    [Fact]
    public void SelectK_WithBic_FindsOptimalClusterCount()
    {
        var cluster1 = CreateCluster(center: [0f, 0f], count: 30, spread: 0.3f, seed: 1);
        var cluster2 = CreateCluster(center: [10f, 10f], count: 30, spread: 0.3f, seed: 2);
        var cluster3 = CreateCluster(center: [20f, 0f], count: 30, spread: 0.3f, seed: 3);
        var data = cluster1.Concat(cluster2).Concat(cluster3).ToArray();

        var optimalK = GaussianMixtureModel.SelectK(data, maxK: 6);

        Assert.InRange(optimalK, 2, 4);
    }

    [Fact]
    public void SelectK_DoesNotIsolateEveryPoint_OnDistinctData()
    {
        // #333: a singleton cluster's variance floors to VarianceFloor (1e-6), so its Gaussian
        // log-density at its own mean is -0.5*d*ln(2*pi*1e-6) ~ +47.9 nats at d=8. Through
        // -2*logLikelihood that is ~95.8 of BIC gain per isolated point, against a penalty of only
        // 17*ln(n) ~ 39.1 at n=10. Splitting always won, so SelectK returned k = n for every n
        // from 2 to 10, and the tree loop could never reduce its level count.
        var rng = new Random(Seed: 7);
        for (var n = 2; n <= 10; n++)
        {
            var data = new float[n][];
            for (var i = 0; i < n; i++)
            {
                data[i] = new float[8];
                for (var d = 0; d < 8; d++)
                    data[i][d] = (float)rng.NextDouble();
            }

            var k = GaussianMixtureModel.SelectK(data, maxK: System.Math.Min(n, 10));

            Assert.True(k < n, $"n={n}: SelectK returned k={k}; k must be below n or no tree level can ever reduce");
        }
    }

    [Fact]
    public void SelectK_StillSeparates_WellSeparatedClusters()
    {
        // The fix must not buy termination by making SelectK useless. Two tight, far-apart blobs
        // must still be found as two clusters.
        var rng = new Random(Seed: 11);
        var data = new float[20][];
        for (var i = 0; i < 20; i++)
        {
            data[i] = new float[8];
            var offset = i < 10 ? 0.0f : 10.0f;
            for (var d = 0; d < 8; d++)
                data[i][d] = offset + (float)(rng.NextDouble() * 0.01);
        }

        var k = GaussianMixtureModel.SelectK(data, maxK: 10);

        Assert.True(k >= 2, $"SelectK returned k={k}; two well-separated blobs must yield at least 2 clusters");
    }

    [Fact]
    public void Fit_SingleCluster_AssignsAllToSameLabel()
    {
        var data = CreateCluster(center: [5f, 5f], count: 20, spread: 0.5f, seed: 42);

        var result = GaussianMixtureModel.Fit(data, k: 1);

        Assert.All(result.Assignments, a => Assert.Equal(0, a));
    }

    [Fact]
    public void Fit_SinglePoint_DoesNotThrow()
    {
        var data = new[] { new[] { 0f, 0f } };
        var result = GaussianMixtureModel.Fit(data, k: 1);
        Assert.Single(result.Assignments);
        Assert.Equal(0, result.Assignments[0]);
    }

    [Fact]
    public void Fit_AllIdenticalPoints_DoesNotThrow()
    {
        var data = Enumerable.Range(0, 10)
            .Select(_ => new[] { 5f, 5f })
            .ToArray();
        var result = GaussianMixtureModel.Fit(data, k: 2);
        Assert.Equal(10, result.Assignments.Length);
    }

    [Fact]
    public void Fit_MoreClustersThanPoints_DoesNotThrow()
    {
        var data = new[] { new[] { 0f, 0f }, new[] { 1f, 1f } };
        var result = GaussianMixtureModel.Fit(data, k: 5);
        Assert.Equal(2, result.Assignments.Length);
    }

    private static float[][] CreateCluster(float[] center, int count, float spread, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => center.Select(c => c + (float)(rng.NextDouble() - 0.5) * spread * 2).ToArray())
            .ToArray();
    }
}
