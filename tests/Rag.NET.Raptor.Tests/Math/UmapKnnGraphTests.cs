using Rag.NET.Raptor.Math;
using Xunit;

namespace Rag.NET.Raptor.Tests.Math;

/// <summary>
/// Pins <see cref="Umap.BuildKnnGraph"/> against a brute-force reference (#348).
/// </summary>
/// <remarks>
/// <para>
/// The rest of <see cref="UmapTests"/> asserts only the shape of <c>Fit</c>'s result — row count and
/// dimensionality. None of it would fail if the neighbour graph were wrong, because <c>Fit</c>'s
/// output passes through a stochastic layout optimisation that turns a wrong neighbour set into
/// coordinates that still look plausible. So the optimisation in #348 gets its own gate here,
/// against a reference implementation rather than against a property.
/// </para>
/// <para>
/// <see cref="BruteForceKnn"/> is the pre-#348 implementation, kept verbatim. It is the thing being
/// preserved, so it is worth more as a literal copy than as a tidied one.
/// </para>
/// </remarks>
public class UmapKnnGraphTests
{
    [Theory]
    [InlineData(40, 8, 5)]
    [InlineData(100, 16, 15)]
    [InlineData(257, 32, 15)]
    [InlineData(60, 384, 15)]
    [InlineData(20, 12, 19)]
    public void BuildKnnGraph_OnDistinctPoints_MatchesBruteForceExactly(int n, int dims, int k)
    {
        var data = RandomData(n, dims, seed: 7);

        var (indices, distances) = Umap.BuildKnnGraph(data, k);
        var (expectedIndices, expectedDistances) = BruteForceKnn(data, k);

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(expectedIndices[i], indices[i]);
            Assert.Equal(expectedDistances[i], distances[i]);
        }
    }

    [Fact]
    public void BuildKnnGraph_ReturnsDistancesInAscendingOrder()
    {
        // BuildDirectedEdges reads knnDistances[i][0] as rho, the nearest-neighbour distance.
        // Ascending order is a contract, not an accident of whichever selection is used.
        var (_, distances) = Umap.BuildKnnGraph(RandomData(80, 24, seed: 11), k: 15);

        foreach (var row in distances)
        {
            for (int j = 1; j < row.Length; j++)
            {
                Assert.True(row[j] >= row[j - 1], $"distances not ascending at {j}: {row[j - 1]} then {row[j]}");
            }
        }
    }

    [Fact]
    public void BuildKnnGraph_NeverSelectsThePointItself()
    {
        var (indices, _) = Umap.BuildKnnGraph(RandomData(50, 16, seed: 3), k: 15);

        for (int i = 0; i < indices.Length; i++)
        {
            Assert.DoesNotContain(i, indices[i]);
            Assert.Equal(indices[i].Length, DistinctCount(indices[i]));
        }
    }

    [Fact]
    public void BuildKnnGraph_WithDuplicatePoints_ReturnsACorrectNeighbourSet()
    {
        // Duplicate chunks in a corpus embed identically, so distances tie exactly. Which tied
        // neighbour wins was already arbitrary before #348 — Array.Sort is an unstable introsort —
        // so index equality is not the invariant here and asserting it would pin an accident.
        // What must hold is that the k distances are the k smallest, which is tie-independent.
        var data = WithDuplicates(distinctCount: 20, copies: 4, dims: 16, seed: 5);
        int k = 15;

        var (indices, distances) = Umap.BuildKnnGraph(data, k);
        var (_, expectedDistances) = BruteForceKnn(data, k);

        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(expectedDistances[i], distances[i]);
            Assert.DoesNotContain(i, indices[i]);
            Assert.Equal(k, DistinctCount(indices[i]));

            // Each reported distance is genuinely the distance to the index reported beside it.
            for (int j = 0; j < k; j++)
            {
                Assert.Equal(Distance(data[i], data[indices[i][j]]), distances[i][j], tolerance: 1e-6f);
            }
        }
    }

    [Fact]
    public void BuildKnnGraph_WhenEveryPointIsIdentical_ReturnsZeroDistances()
    {
        var data = Enumerable.Range(0, 25).Select(_ => new[] { 1f, 2f, 3f, 4f }).ToArray();

        var (indices, distances) = Umap.BuildKnnGraph(data, k: 15);

        for (int i = 0; i < data.Length; i++)
        {
            Assert.All(distances[i], d => Assert.Equal(0f, d));
            Assert.DoesNotContain(i, indices[i]);
            Assert.Equal(15, DistinctCount(indices[i]));
        }
    }

    [Fact]
    public void BuildKnnGraph_WithMoreNeighboursThanPoints_Throws()
    {
        // Fit clamps k to n - 1, so this is unreachable through the public path. It is guarded
        // anyway because the previous implementation answered it silently and wrongly: it padded
        // the row with the point's own float.MaxValue self-distance, and rho — knnDistances[i][0] —
        // would have been read off a graph containing that sentinel.
        var data = RandomData(5, 8, seed: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => Umap.BuildKnnGraph(data, k: 5));
    }

    // ── Brute-force reference: the pre-#348 implementation, verbatim ──────────────────

    private static (int[][] Indices, float[][] Distances) BruteForceKnn(float[][] data, int k)
    {
        int n = data.Length;
        var indices = new int[n][];
        var distances = new float[n][];

        for (int i = 0; i < n; i++)
        {
            var dists = new (float Distance, int Index)[n];
            for (int j = 0; j < n; j++)
            {
                dists[j] = (i == j ? float.MaxValue : Distance(data[i], data[j]), j);
            }

            Array.Sort(dists, (a, b) => a.Distance.CompareTo(b.Distance));

            indices[i] = new int[k];
            distances[i] = new float[k];
            for (int j = 0; j < k; j++)
            {
                indices[i][j] = dists[j].Index;
                distances[i][j] = dists[j].Distance;
            }
        }

        return (indices, distances);
    }

    /// <summary>Distinct count without LINQ — ZA0601 rejects LINQ inside a loop.</summary>
    private static int DistinctCount(int[] values) => new HashSet<int>(values).Count;

    private static float Distance(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }

        return MathF.Sqrt(sum);
    }

    private static float[][] RandomData(int count, int dims, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => Enumerable.Range(0, dims).Select(_ => (float)((rng.NextDouble() * 2.0) - 1.0)).ToArray())
            .ToArray();
    }

    private static float[][] WithDuplicates(int distinctCount, int copies, int dims, int seed)
    {
        var distinct = RandomData(distinctCount, dims, seed);
        return distinct.SelectMany(row => Enumerable.Repeat(row, copies)).ToArray();
    }
}
