namespace Rag.NET.Raptor.Math;

/// <summary>
/// Minimal UMAP (Uniform Manifold Approximation and Projection) implementation
/// for dimensionality reduction prior to GMM clustering. Not intended for visualization.
/// </summary>
internal static class Umap
{
    private const float RepulsionStrength = 1.0f;

    /// <summary>Row count at or above which <see cref="BuildKnnGraph"/> parallelises across rows.</summary>
    /// <remarks>
    /// Deliberately well above the break-even point. The graph costs n squared distance
    /// evaluations, so at this size it is already tens of milliseconds and partitioning overhead is
    /// noise — but a per-document RAPTOR tree holds tens of chunks, and there are hundreds of those
    /// per corpus. Setting the threshold high leaves every one of them on exactly the sequential
    /// path they already took, so the only workload whose timing changes is the corpus-scale one
    /// this is for (#348). The result is identical on both paths.
    /// </remarks>
    private const int ParallelRowThreshold = 512;

    /// <summary>
    /// Reduces high-dimensional data to <paramref name="targetDimensions"/> dimensions
    /// using a simplified UMAP algorithm.
    /// </summary>
    internal static float[][] Fit(
        float[][] data,
        int targetDimensions,
        int nNeighbors = 15,
        float minDist = 0.1f,
        int nEpochs = 200)
    {
        ArgumentNullException.ThrowIfNull(data);

        int n = data.Length;
        if (n == 0)
        {
            return [];
        }

        int dims = data[0].Length;
        if (targetDimensions >= dims)
        {
            return CopyData(data, targetDimensions);
        }

        int k = System.Math.Min(nNeighbors, n - 1);
        if (k < 1)
        {
            return CreateRandomEmbedding(n, targetDimensions, seed: 42);
        }

        var (knnIndices, knnDistances) = BuildKnnGraph(data, k);
        var graph = BuildFuzzyGraph(knnIndices, knnDistances, k);
        var embedding = CreateRandomEmbedding(n, targetDimensions, seed: 42);
        OptimizeLayout(embedding, graph, nEpochs, minDist, targetDimensions);

        return embedding;
    }

    private static float[][] CopyData(float[][] data, int targetDimensions)
    {
        var result = new float[data.Length][];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = new float[targetDimensions];
            int copyLen = System.Math.Min(data[i].Length, targetDimensions);
            Array.Copy(data[i], result[i], copyLen);
        }

        return result;
    }

    /// <summary>
    /// Builds the k-nearest-neighbour graph: for each row, the <paramref name="k"/> closest other
    /// rows in ascending distance order (<see cref="BuildDirectedEdges"/> reads element 0 as rho).
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so it can be measured and tested directly. It is
    /// the quadratic step in <see cref="Fit"/>, and the tests around it compare its output against a
    /// brute-force reference — neither is reachable through <see cref="Fit"/>, whose result passes
    /// through a stochastic layout optimisation that would mask a wrong neighbour set entirely.
    /// </remarks>
    internal static (int[][] Indices, float[][] Distances) BuildKnnGraph(float[][] data, int k)
    {
        int n = data.Length;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(k, n - 1);

        var indices = new int[n][];
        var distances = new float[n][];

        if (n >= ParallelRowThreshold)
        {
            // Row i writes only indices[i] and distances[i] and reads nothing another row writes,
            // so the result does not depend on scheduling: the same graph comes back either way.
            Parallel.For(0, n, i => (indices[i], distances[i]) = NearestNeighborsOf(data, i, k));
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                (indices[i], distances[i]) = NearestNeighborsOf(data, i, k);
            }
        }

        return (indices, distances);
    }

    /// <summary>
    /// The <paramref name="k"/> rows nearest <paramref name="i"/>, ascending, excluding itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selection, not a sort. The previous implementation materialised every distance from row
    /// <paramref name="i"/> into a <c>(float, int)[n]</c> and sorted the whole thing to read the
    /// first <paramref name="k"/> entries. At MultiHop-RAG's corpus scale that buffer is 141,184
    /// bytes — over the 85,000-byte large-object-heap threshold — allocated once per row, which is
    /// roughly 2.5 GB of LOH traffic for a single corpus-level reduction (#348). A bounded heap
    /// needs <paramref name="k"/> slots regardless of <c>n</c>, so the scratch buffer disappears
    /// rather than being pooled: the arrays below are the returned result, not a temporary.
    /// </para>
    /// <para>
    /// Comparison is on the <em>squared</em> distance, and <see cref="MathF.Sqrt"/> runs only on the
    /// <paramref name="k"/> survivors. Square root is monotonic over non-negative values, so the
    /// ordering is unchanged and each returned distance is bit-identical to the previous
    /// implementation's — it is the same <c>MathF.Sqrt</c> of the same accumulated sum.
    /// </para>
    /// <para>
    /// Exact ties — duplicate chunks embed identically — may resolve to different neighbours than
    /// before. That was never fixed: <see cref="Array.Sort{T}(T[], Comparison{T})"/> is an unstable
    /// introsort whose tie order depends on <c>n</c>. The set of distances is identical either way.
    /// </para>
    /// </remarks>
    private static (int[] Indices, float[] Distances) NearestNeighborsOf(float[][] data, int i, int k)
    {
        // Max-heap keyed on distance: the worst of the current best-k sits at the root, so a
        // candidate is rejected or accepted in a single comparison against it.
        var heapDistances = new float[k];
        var heapIndices = new int[k];
        int count = 0;
        var origin = data[i];

        for (int j = 0; j < data.Length; j++)
        {
            if (j == i)
            {
                continue;
            }

            float squared = SquaredEuclideanDistance(origin, data[j]);
            if (count < k)
            {
                heapDistances[count] = squared;
                heapIndices[count] = j;
                count++;
                if (count == k)
                {
                    Heapify(heapDistances, heapIndices, k);
                }
            }
            else if (squared < heapDistances[0])
            {
                heapDistances[0] = squared;
                heapIndices[0] = j;
                SiftDown(heapDistances, heapIndices, start: 0, end: k);
            }
        }

        // k <= n - 1 is enforced above, so every slot is filled and the heap was built.
        return SortHeapAscending(heapDistances, heapIndices, k);
    }

    /// <summary>Heap-sorts in place, then converts the squared distances to real ones.</summary>
    private static (int[] Indices, float[] Distances) SortHeapAscending(
        float[] heapDistances, int[] heapIndices, int count)
    {
        for (int end = count - 1; end > 0; end--)
        {
            // Repeatedly moving the largest to the back leaves the array ascending, which
            // BuildDirectedEdges depends on: it reads element 0 as rho, the nearest distance.
            Swap(heapDistances, heapIndices, 0, end);
            SiftDown(heapDistances, heapIndices, start: 0, end: end);
        }

        // Span, and foreach by ref: Span's enumerator yields ref float, so the in-place rewrite is
        // expressible as a foreach — which is what HLQ013 asks for over an index loop.
        foreach (ref float distance in heapDistances.AsSpan(0, count))
        {
            distance = MathF.Sqrt(distance);
        }

        return (heapIndices, heapDistances);
    }

    private static void Heapify(float[] distances, int[] indices, int count)
    {
        for (int start = (count / 2) - 1; start >= 0; start--)
        {
            SiftDown(distances, indices, start, count);
        }
    }

    private static void SiftDown(float[] distances, int[] indices, int start, int end)
    {
        int root = start;
        while (true)
        {
            int child = (2 * root) + 1;
            if (child >= end)
            {
                return;
            }

            if (child + 1 < end && distances[child + 1] > distances[child])
            {
                child++;
            }

            if (distances[root] >= distances[child])
            {
                return;
            }

            Swap(distances, indices, root, child);
            root = child;
        }
    }

    private static void Swap(float[] distances, int[] indices, int a, int b)
    {
        (distances[a], distances[b]) = (distances[b], distances[a]);
        (indices[a], indices[b]) = (indices[b], indices[a]);
    }

    private static List<(int Row, int Col, float Weight)> BuildFuzzyGraph(
        int[][] knnIndices,
        float[][] knnDistances,
        int k)
    {
        int n = knnIndices.Length;
        var sigmas = ComputeSigmas(knnDistances, k);
        var edgeDict = BuildDirectedEdges(knnIndices, knnDistances, sigmas, k, n);
        return SymmetrizeEdges(edgeDict);
    }

    private static float[] ComputeSigmas(float[][] knnDistances, int k)
    {
        int n = knnDistances.Length;
        var sigmas = new float[n];
        float targetLog = MathF.Log2(k);

        for (int i = 0; i < n; i++)
        {
            sigmas[i] = FindSigma(knnDistances[i], targetLog);
        }

        return sigmas;
    }

    private static Dictionary<(int, int), float> BuildDirectedEdges(
        int[][] knnIndices,
        float[][] knnDistances,
        float[] sigmas,
        int k,
        int n)
    {
        var edgeDict = new Dictionary<(int, int), float>();

        for (int i = 0; i < n; i++)
        {
            float rho = knnDistances[i][0];
            for (int j = 0; j < k; j++)
            {
                int neighbor = knnIndices[i][j];
                float dist = knnDistances[i][j];
                float adjustedDist = System.Math.Max(0, dist - rho);
                float weight = MathF.Exp(-adjustedDist / sigmas[i]);
                edgeDict[(i, neighbor)] = weight;
            }
        }

        return edgeDict;
    }

    private static List<(int Row, int Col, float Weight)> SymmetrizeEdges(
        Dictionary<(int, int), float> edgeDict)
    {
        var symmetricEdges = new Dictionary<(int, int), float>();

        foreach (var ((row, col), weight) in edgeDict)
        {
            var canonical = row < col ? (row, col) : (col, row);
            if (symmetricEdges.ContainsKey(canonical))
            {
                continue;
            }

            float wIj = weight;
            float wJi = edgeDict.GetValueOrDefault((col, row));
            float symmetric = wIj + wJi - (wIj * wJi);
            if (symmetric > 1e-6f)
            {
                symmetricEdges[canonical] = symmetric;
            }
        }

        var edges = new List<(int Row, int Col, float Weight)>(symmetricEdges.Count * 2);
        foreach (var ((row, col), w) in symmetricEdges)
        {
            edges.Add((row, col, w));
            edges.Add((col, row, w));
        }

        return edges;
    }

    private static float FindSigma(float[] distances, float target)
    {
        const int maxIterations = 64;
        const float tolerance = 1e-5f;

        float lo = 1e-10f;
        float hi = 1000f;
        float mid = 1f;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            mid = (lo + hi) / 2f;
            float pSum = 0;
            foreach (float d in distances)
            {
                pSum += MathF.Exp(-d / mid);
            }

            float logVal = MathF.Log2(pSum);

            if (MathF.Abs(logVal - target) < tolerance)
            {
                break;
            }

            if (logVal > target)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return mid;
    }

    private static float[][] CreateRandomEmbedding(int n, int targetDimensions, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, n)
            .Select(_ => CreateRandomPoint(rng, targetDimensions))
            .ToArray();
    }

    private static float[] CreateRandomPoint(Random rng, int targetDimensions)
    {
        return Enumerable.Range(0, targetDimensions)
            .Select(_ => (float)(rng.NextGaussian() * 1e-2))
            .ToArray();
    }

    private static void OptimizeLayout(
        float[][] embedding,
        List<(int Row, int Col, float Weight)> graph,
        int nEpochs,
        float minDist,
        int targetDimensions)
    {
        if (graph.Count == 0)
        {
            return;
        }

        var (a, b) = FindAbParams(minDist);
        var (epochsPerSample, epochOfNextSample) = BuildEpochSchedule(graph);
        var rng = new Random(42);
        int n = embedding.Length;

        for (int epoch = 0; epoch < nEpochs; epoch++)
        {
            float alpha = 1.0f - ((float)epoch / nEpochs);
            ProcessEpoch(embedding, graph, epochsPerSample, epochOfNextSample, epoch, alpha, a, b, targetDimensions, n, rng);
        }
    }

    private static (float[] EpochsPerSample, float[] EpochOfNextSample) BuildEpochSchedule(
        List<(int Row, int Col, float Weight)> graph)
    {
        float maxWeight = 0;
        foreach (var (_, _, weight) in graph)
        {
            if (weight > maxWeight)
            {
                maxWeight = weight;
            }
        }

        var epochsPerSample = new float[graph.Count];
        for (int i = 0; i < graph.Count; i++)
        {
            epochsPerSample[i] = maxWeight / graph[i].Weight;
        }

        var epochOfNextSample = new float[graph.Count];
        Array.Copy(epochsPerSample, epochOfNextSample, graph.Count);

        return (epochsPerSample, epochOfNextSample);
    }

    private static void ProcessEpoch(
        float[][] embedding,
        List<(int Row, int Col, float Weight)> graph,
        float[] epochsPerSample,
        float[] epochOfNextSample,
        int epoch,
        float alpha,
        float a,
        float b,
        int targetDimensions,
        int n,
        Random rng)
    {
        for (int edgeIdx = 0; edgeIdx < graph.Count; edgeIdx++)
        {
            if (epochOfNextSample[edgeIdx] > epoch)
            {
                continue;
            }

            int i = graph[edgeIdx].Row;
            int j = graph[edgeIdx].Col;

            ApplyAttractiveForce(embedding, i, j, alpha, a, b, targetDimensions);
            ApplyRepulsiveForce(embedding, i, alpha, a, b, targetDimensions, n, rng);

            epochOfNextSample[edgeIdx] += epochsPerSample[edgeIdx];
        }
    }

    private static void ApplyAttractiveForce(
        float[][] embedding, int i, int j, float alpha, float a, float b, int targetDimensions)
    {
        float distSq = SquaredEuclideanDistance(embedding[i], embedding[j]);

        float gradCoeff = 0;
        if (distSq > 0)
        {
            gradCoeff = -2.0f * a * b * MathF.Pow(distSq, b - 1.0f);
            gradCoeff /= 1.0f + (a * MathF.Pow(distSq, b));
        }

        for (int d = 0; d < targetDimensions; d++)
        {
            float gradD = Clamp(gradCoeff * (embedding[i][d] - embedding[j][d]));
            embedding[i][d] += alpha * gradD;
            embedding[j][d] -= alpha * gradD;
        }
    }

    private static void ApplyRepulsiveForce(
        float[][] embedding, int i, float alpha, float a, float b, int targetDimensions, int n, Random rng)
    {
        int negIdx = rng.Next(n);
        if (negIdx == i)
        {
            return;
        }

        float negDistSq = SquaredEuclideanDistance(embedding[i], embedding[negIdx]);

        float repGradCoeff = 0;
        if (negDistSq > 0)
        {
            repGradCoeff = 2.0f * RepulsionStrength * b;
            repGradCoeff /= (0.001f + negDistSq) * (1.0f + (a * MathF.Pow(negDistSq, b)));
        }

        for (int d = 0; d < targetDimensions; d++)
        {
            float gradD = Clamp(repGradCoeff * (embedding[i][d] - embedding[negIdx][d]));
            embedding[i][d] += alpha * gradD;
        }
    }

    private static float SquaredEuclideanDistance(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }

    private static float Clamp(float value)
    {
        return System.Math.Clamp(value, -4.0f, 4.0f);
    }

    private static (float A, float B) FindAbParams(float minDist)
    {
        if (minDist >= 0.99f)
        {
            return (1.0f, 1.0f);
        }

        float b = 1.0f;
        float dPow = MathF.Pow(minDist, 2 * b);
        float a = dPow > 1e-10f ? 1.0f / dPow : 1e10f;
        return (a, b);
    }
}
