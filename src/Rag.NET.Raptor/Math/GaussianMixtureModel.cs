using System.Diagnostics.CodeAnalysis;

namespace Rag.NET.Raptor.Math;

[SuppressMessage("Performance", "HLQ013:Use foreach loop", Justification = "Index-based access required for matrix operations")]
internal static class GaussianMixtureModel
{
    /// <summary>
    /// The absolute last resort, used only when the data itself has no spread at all.
    /// </summary>
    /// <remarks>
    /// <b>This used to be THE floor, and it was an absolute constant on relative data (#337).</b>
    /// A variance of <c>1e-6</c> is a standard deviation of 0.001 — far tighter than any real
    /// cluster of unit-scale embeddings — so a component of near-identical vectors floored and
    /// scored as a near-perfect fit. Measured before the fix, on 20 points containing five
    /// near-identical pairs, <c>SelectK</c> returned the maximum k of <b>10</b>: it isolated
    /// everything it could. Scaling the same three-blob geometry down by 1,000 put the whole
    /// dataset under the floor and collapsed k from 3 to <b>1</b> — the same cause, the opposite
    /// symptom. It survives only to keep a genuinely zero-variance dataset from dividing by zero.
    /// </remarks>
    private const double AbsoluteVarianceFloor = 1e-12;

    /// <summary>
    /// The fraction of the data's own mean variance below which a component is not allowed to go.
    /// </summary>
    /// <remarks>
    /// A hundredth: tight enough that genuinely tight clusters are still expressible, loose enough
    /// that a degenerate component cannot claim unbounded likelihood. The floor exists to avoid a
    /// division by zero; it does not need to be a value that makes a collapsed component look
    /// excellent.
    /// </remarks>
    private const double VarianceFloorFraction = 0.001;
    private const double EmptyClusterThreshold = 1e-10;

    // The smallest component that is a cluster rather than a memorised point. Measured in hard
    // assignments, not in summed responsibilities — see IsDegenerateFit for both that distinction
    // and the reason a lone undersized component does not by itself disqualify a candidate.
    private const int MinimumComponentPoints = 2;

    internal static GmmResult Fit(float[][] data, int k, int maxIterations = 100, double tolerance = 1e-6)
    {
        int n = data.Length;
        int d = data[0].Length;

        double varianceFloor = ComputeVarianceFloor(data, n, d);

        double[][] means = KMeansPlusPlusInit(data, k, d);
        double[][] variances = InitializeVariances(k, d, varianceFloor);
        double[] weights = InitializeWeights(k);
        double[][] responsibilities = InitializeResponsibilities(n, k);

        RunEmIterations(data, k, n, d, means, variances, weights, responsibilities, maxIterations, tolerance, varianceFloor);

        return BuildResult(responsibilities, n, k);
    }

    internal static int SelectK(float[][] data, int maxK, int maxIterations = 100)
    {
        int n = data.Length;
        int d = data[0].Length;
        double bestBic = double.PositiveInfinity;
        int bestK = 1;

        for (int k = 1; k <= maxK; k++)
        {
            var result = Fit(data, k, maxIterations);

            if (IsDegenerateFit(result, k))
            {
                continue;
            }

            double logLikelihood = ComputeLogLikelihood(data, result, k, d);
            int numParams = k * (2 * d + 1) - 1;
            double bic = -2.0 * logLikelihood + numParams * System.Math.Log(n);

            if (bic < bestBic)
            {
                bestBic = bic;
                bestK = k;
            }
        }

        return bestK;
    }

    /// <summary>
    /// Reports whether <paramref name="gmmResult"/> is a fit BIC has no vocabulary to reject.
    /// </summary>
    /// <remarks>
    /// Two independent disqualifications, because BIC scores both as excellent fits (#333).
    ///
    /// An empty component means <paramref name="k"/> overstates the model actually fitted, so its
    /// parameter count — and therefore its penalty — is simply wrong for what was fitted.
    ///
    /// A component owning a single point has no spread to estimate: its variance collapses to the
    /// floor and its log-density at its own mean climbs accordingly. The figures below were
    /// measured against the ORIGINAL absolute floor of <c>1e-6</c>, which reached roughly +47.9
    /// nats at eight dimensions — about 95.8 of BIC gain through <c>-2 * logLikelihood</c> against
    /// a penalty of only some 39.1, so splitting always won and <c>SelectK</c> returned k = n for
    /// every n from 2 to 10.
    ///
    /// <b>The floor is now a fraction of the data's own variance (#337), so the exact figures no
    /// longer hold — but the shape does, and this rule is still what rejects the fit.</b> A
    /// data-scaled floor bounds how good a collapsed component can look; it does not stop one
    /// looking better than it should.
    ///
    /// The test is on the *share of points* those components hold rather than on their mere
    /// existence. A single genuine outlier is a fact about the data, not a broken fit: on two tight
    /// blobs plus one far-away point, every k from 2 to 7 isolates that point, so disqualifying any
    /// fit containing one left k = 1 as the only candidate and no tree could be built at all. What
    /// distinguishes the #333 pathology is not that some component is alone but that most points
    /// are — a fit where half the data sits in components of one is fragmentation, not clustering.
    ///
    /// Counts hard assignments rather than summing responsibilities. It is exact: a component that
    /// genuinely owns exactly two points sums its responsibilities to slightly under two (measured:
    /// 1.9988) because the other components keep a sliver of the mass, so a threshold on the soft
    /// count rejects real clusters unless it carries an epsilon nobody can justify. And it matches
    /// what happens downstream: <c>RaptorIngestionBehavior</c> groups by
    /// <see cref="GmmResult.Assignments"/>, so the hard counts are the sizes of the clusters that
    /// actually get summarised.
    /// </remarks>
    /// <param name="gmmResult">The fit to inspect.</param>
    /// <param name="k">The component count the fit used.</param>
    /// <returns>Whether the fit is too degenerate to be scored against other candidates.</returns>
    private static bool IsDegenerateFit(in GmmResult gmmResult, int k)
    {
        int[] counts = new int[k];
        foreach (int assignment in gmmResult.Assignments)
        {
            counts[assignment]++;
        }

        int pointsInRealComponents = 0;
        for (int j = 0; j < k; j++)
        {
            if (counts[j] == 0)
            {
                return true;
            }

            if (counts[j] >= MinimumComponentPoints)
            {
                pointsInRealComponents += counts[j];
            }
        }

        return pointsInRealComponents * 2 <= gmmResult.Assignments.Length;
    }

    private static double[][] InitializeVariances(int k, int d, double varianceFloor)
    {
        double[][] variances = new double[k][];
        for (int j = 0; j < k; j++)
        {
            variances[j] = new double[d];
            Array.Fill(variances[j], 1.0);
        }

        return variances;
    }

    private static double[] InitializeWeights(int k)
    {
        double[] weights = new double[k];
        Array.Fill(weights, 1.0 / k);
        return weights;
    }

    private static double[][] InitializeResponsibilities(int n, int k)
    {
        double[][] responsibilities = new double[n][];
        for (int i = 0; i < n; i++)
        {
            responsibilities[i] = new double[k];
        }

        return responsibilities;
    }

    private static void RunEmIterations(
        float[][] data, int k, int n, int d,
        double[][] means, double[][] variances, double[] weights,
        double[][] responsibilities, int maxIterations, double tolerance, double varianceFloor)
    {
        double prevLogLikelihood = double.NegativeInfinity;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double logLikelihood = EStep(data, k, n, d, means, variances, weights, responsibilities);

            if (System.Math.Abs(logLikelihood - prevLogLikelihood) < tolerance)
            {
                break;
            }

            prevLogLikelihood = logLikelihood;
            MStep(data, k, n, d, means, variances, weights, responsibilities, varianceFloor);
        }
    }

    private static double EStep(
        float[][] data, int k, int n, int d,
        double[][] means, double[][] variances, double[] weights,
        double[][] responsibilities)
    {
        double logLikelihood = 0.0;
        for (int i = 0; i < n; i++)
        {
            double[] logProbs = new double[k];
            for (int j = 0; j < k; j++)
            {
                logProbs[j] = System.Math.Log(weights[j]) + LogGaussianDiag(data[i], means[j], variances[j], d);
            }

            double lse = LogSumExp(logProbs);
            logLikelihood += lse;

            for (int j = 0; j < k; j++)
            {
                responsibilities[i][j] = System.Math.Exp(logProbs[j] - lse);
            }
        }

        return logLikelihood;
    }

    private static void MStep(
        float[][] data, int k, int n, int d,
        double[][] means, double[][] variances, double[] weights,
        double[][] responsibilities, double varianceFloor)
    {
        for (int j = 0; j < k; j++)
        {
            double nk = ComputeEffectiveCount(responsibilities, n, j);

            if (nk < EmptyClusterThreshold)
            {
                weights[j] = 0.0;
                continue;
            }

            weights[j] = nk / n;
            UpdateMeans(data, responsibilities, means[j], n, d, j, nk);
            UpdateVariances(data, responsibilities, means[j], variances[j], n, d, j, nk, varianceFloor);
        }
    }

    private static double ComputeEffectiveCount(double[][] responsibilities, int n, int j)
    {
        double nk = 0.0;
        for (int i = 0; i < n; i++)
        {
            nk += responsibilities[i][j];
        }

        return nk;
    }

    private static void UpdateMeans(
        float[][] data, double[][] responsibilities, double[] mean,
        int n, int d, int j, double nk)
    {
        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                sum += responsibilities[i][j] * data[i][di];
            }

            mean[di] = sum / nk;
        }
    }

    /// <summary>
    /// The variance floor for one dataset: a fraction of its own mean per-dimension variance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Computed once per <see cref="Fit"/> from the data, not fixed in advance (#337).</b> The
    /// floor's job is to stop a component's variance reaching zero and its log-density reaching
    /// infinity. What counts as "near zero" is a property of the data's scale, and embeddings are
    /// not required to be unit-scale — so a constant is a yardstick that is either far too coarse
    /// or far too fine depending on the corpus, and was measurably both.
    /// </para>
    /// <para>
    /// Uses the population variance about the global mean, per dimension, averaged. That is the
    /// spread the model is trying to explain, so a component is floored relative to the thing it
    /// is a component OF.
    /// </para>
    /// </remarks>
    private static double ComputeVarianceFloor(float[][] data, int n, int d)
    {
        double totalVariance = 0.0;

        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
                sum += data[i][di];

            double mean = sum / n;
            double squared = 0.0;
            for (int i = 0; i < n; i++)
            {
                double diff = data[i][di] - mean;
                squared += diff * diff;
            }

            totalVariance += squared / n;
        }

        double scaled = totalVariance / d * VarianceFloorFraction;

        // A dataset of identical points has no spread to take a fraction of, and that is the one
        // case the absolute floor still exists for.
        return System.Math.Max(scaled, AbsoluteVarianceFloor);
    }

    private static void UpdateVariances(
        float[][] data, double[][] responsibilities, double[] mean, double[] variance,
        int n, int d, int j, double nk, double varianceFloor)
    {
        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double diff = data[i][di] - mean[di];
                sum += responsibilities[i][j] * diff * diff;
            }

            variance[di] = System.Math.Max(sum / nk, varianceFloor);
        }
    }

    private static GmmResult BuildResult(double[][] responsibilities, int n, int k)
    {
        int[] assignments = new int[n];
        float[][] result = new float[n][];
        for (int i = 0; i < n; i++)
        {
            result[i] = new float[k];
            int bestJ = 0;
            double bestVal = responsibilities[i][0];
            for (int j = 0; j < k; j++)
            {
                result[i][j] = (float)responsibilities[i][j];
                if (responsibilities[i][j] > bestVal)
                {
                    bestVal = responsibilities[i][j];
                    bestJ = j;
                }
            }

            assignments[i] = bestJ;
        }

        return new GmmResult(assignments, result);
    }

    private static double ComputeLogLikelihood(float[][] data, in GmmResult gmmResult, int k, int d)
    {
        int n = data.Length;
        var (means, variances, weights) = ReconstructParameters(data, gmmResult, k, d, n, ComputeVarianceFloor(data, n, d));

        double logLikelihood = 0.0;
        for (int i = 0; i < n; i++)
        {
            double[] logProbs = new double[k];
            for (int j = 0; j < k; j++)
            {
                logProbs[j] = System.Math.Log(System.Math.Max(weights[j], 1e-300))
                    + LogGaussianDiag(data[i], means[j], variances[j], d);
            }

            logLikelihood += LogSumExp(logProbs);
        }

        return logLikelihood;
    }

    private static (double[][] Means, double[][] Variances, double[] Weights) ReconstructParameters(
        float[][] data, in GmmResult gmmResult, int k, int d, int n, double varianceFloor)
    {
        double[][] means = new double[k][];
        double[][] variances = new double[k][];
        double[] weights = new double[k];

        for (int j = 0; j < k; j++)
        {
            means[j] = new double[d];
            variances[j] = new double[d];

            double nk = ComputeEffectiveCountFromResult(gmmResult.Responsibilities, n, j);

            if (nk < EmptyClusterThreshold)
            {
                weights[j] = 0.0;
                Array.Fill(variances[j], varianceFloor);
                continue;
            }

            weights[j] = nk / n;
            ReconstructMeansForComponent(data, gmmResult.Responsibilities, means[j], n, d, j, nk);
            ReconstructVariancesForComponent(data, gmmResult.Responsibilities, means[j], variances[j], n, d, j, nk, varianceFloor);
        }

        return (means, variances, weights);
    }

    private static double ComputeEffectiveCountFromResult(float[][] responsibilities, int n, int j)
    {
        double nk = 0.0;
        for (int i = 0; i < n; i++)
        {
            nk += responsibilities[i][j];
        }

        return nk;
    }

    private static void ReconstructMeansForComponent(
        float[][] data, float[][] responsibilities, double[] mean, int n, int d, int j, double nk)
    {
        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                sum += responsibilities[i][j] * data[i][di];
            }

            mean[di] = sum / nk;
        }
    }

    private static void ReconstructVariancesForComponent(
        float[][] data, float[][] responsibilities, double[] mean, double[] variance,
        int n, int d, int j, double nk, double varianceFloor)
    {
        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double diff = data[i][di] - mean[di];
                sum += responsibilities[i][j] * diff * diff;
            }

            variance[di] = System.Math.Max(sum / nk, varianceFloor);
        }
    }

    private static double LogGaussianDiag(float[] x, double[] mean, double[] variance, int d)
    {
        double logDet = 0.0;
        double mahal = 0.0;
        for (int i = 0; i < d; i++)
        {
            logDet += System.Math.Log(variance[i]);
            double diff = x[i] - mean[i];
            mahal += diff * diff / variance[i];
        }

        return -0.5 * (d * System.Math.Log(2.0 * System.Math.PI) + logDet + mahal);
    }

    private static double LogSumExp(double[] values)
    {
        double max = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }

        double sum = 0.0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += System.Math.Exp(values[i] - max);
        }

        return max + System.Math.Log(sum);
    }

    private static double[][] KMeansPlusPlusInit(float[][] data, int k, int d)
    {
        var rng = new Random(42);
        double[][] means = new double[k][];

        int firstIdx = rng.Next(data.Length);
        means[0] = CopyPointToDouble(data[firstIdx], d);

        double[] distances = new double[data.Length];

        for (int j = 1; j < k; j++)
        {
            int chosen = SelectNextCenter(data, means, j, d, distances, rng);
            means[j] = CopyPointToDouble(data[chosen], d);
        }

        return means;
    }

    private static int SelectNextCenter(
        float[][] data, double[][] means, int currentCenters, int d,
        double[] distances, Random rng)
    {
        double totalDist = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            double minDist = double.MaxValue;
            for (int c = 0; c < currentCenters; c++)
            {
                double dist = SquaredDistance(data[i], means[c], d);
                if (dist < minDist)
                {
                    minDist = dist;
                }
            }

            distances[i] = minDist;
            totalDist += minDist;
        }

        double threshold = rng.NextDouble() * totalDist;
        double cumulative = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            cumulative += distances[i];
            if (cumulative >= threshold)
            {
                return i;
            }
        }

        return data.Length - 1;
    }

    private static double SquaredDistance(float[] point, double[] center, int d)
    {
        double dist = 0.0;
        for (int i = 0; i < d; i++)
        {
            double diff = point[i] - center[i];
            dist += diff * diff;
        }

        return dist;
    }

    private static double[] CopyPointToDouble(float[] point, int d)
    {
        double[] result = new double[d];
        for (int i = 0; i < d; i++)
        {
            result[i] = point[i];
        }

        return result;
    }
}
