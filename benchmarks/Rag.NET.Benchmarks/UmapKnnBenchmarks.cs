using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Rag.NET.Raptor.Math;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Measures <see cref="Umap"/> at corpus scale (#348).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="RaptorBenchmarks"/>, which shares one
/// <c>ChunkCount</c> parameter across every method in the class: adding a corpus-scale value
/// there would drag <c>Ingestion_WithRaptor</c> to 17,648 chunks as well.
/// </para>
/// <para>
/// The row counts trace a growth curve rather than a single point. 17,648 is MultiHop-RAG's
/// corpus-scope leaf count, the case #348 is actually about; the smaller values exist so the
/// quadratic term is visible rather than asserted.
/// </para>
/// <para>
/// <see cref="RunStrategy.Monitoring"/> because a single invocation runs for seconds to minutes —
/// far too long for BenchmarkDotNet's default many-invocations-per-iteration strategy.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HLQ013:Use foreach loop", Justification = "Index-based access required for array initialization")]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class UmapKnnBenchmarks
{
    private const int EmbeddingDimensions = 384;
    private const int TargetDimensions = 10;

    /// <summary>Neighbour count: <see cref="Umap.Fit"/>'s <c>nNeighbors</c> default, which the RAPTOR call site never overrides.</summary>
    private const int Neighbors = 15;

    private float[][] _embeddings = null!;

    [Params(2_000, 8_000, 17_648)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup() => _embeddings = BuildEmbeddings(RowCount, EmbeddingDimensions);

    /// <summary>The k-nearest-neighbour graph in isolation — the quadratic step #348 targets.</summary>
    [Benchmark]
    public int Knn_BuildGraph()
    {
        var (indices, _) = Umap.BuildKnnGraph(_embeddings, Neighbors);
        return indices.Length;
    }

    /// <summary>End-to-end, so the kNN step's share of the whole reduction is visible rather than assumed.</summary>
    [Benchmark]
    public int Umap_FitFull() => Umap.Fit(_embeddings, TargetDimensions).Length;

    /// <summary>
    /// Deterministic pseudo-random vectors. Real embeddings would be more faithful, but the
    /// distance loop's cost does not depend on the values, and a fixed seed keeps runs comparable.
    /// </summary>
    private static float[][] BuildEmbeddings(int count, int dimensions)
    {
        var rng = new Random(42);
        var result = new float[count][];
        for (int i = 0; i < count; i++)
        {
            var row = new float[dimensions];
            for (int d = 0; d < dimensions; d++)
            {
                row[d] = (float)((rng.NextDouble() * 2.0) - 1.0);
            }

            result[i] = row;
        }

        return result;
    }
}
