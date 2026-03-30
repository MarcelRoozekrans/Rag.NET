using System.Diagnostics.CodeAnalysis;

namespace Rag.NET.Raptor.Math;

[SuppressMessage("Performance", "HLQ013:Use foreach loop", Justification = "Index-based access required for matrix operations")]
internal static class GaussianMixtureModel
{
    private const double VarianceFloor = 1e-6;
    private const double EmptyClusterThreshold = 1e-10;

    internal static GmmResult Fit(float[][] data, int k, int maxIterations = 100, double tolerance = 1e-6)
    {
        int n = data.Length;
        int d = data[0].Length;

        double[][] means = KMeansPlusPlusInit(data, k, d);
        double[][] variances = InitializeVariances(k, d);
        double[] weights = InitializeWeights(k);
        double[][] responsibilities = InitializeResponsibilities(n, k);

        RunEmIterations(data, k, n, d, means, variances, weights, responsibilities, maxIterations, tolerance);

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

    private static double[][] InitializeVariances(int k, int d)
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
        double[][] responsibilities, int maxIterations, double tolerance)
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
            MStep(data, k, n, d, means, variances, weights, responsibilities);
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
        double[][] responsibilities)
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
            UpdateVariances(data, responsibilities, means[j], variances[j], n, d, j, nk);
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

    private static void UpdateVariances(
        float[][] data, double[][] responsibilities, double[] mean, double[] variance,
        int n, int d, int j, double nk)
    {
        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double diff = data[i][di] - mean[di];
                sum += responsibilities[i][j] * diff * diff;
            }

            variance[di] = System.Math.Max(sum / nk, VarianceFloor);
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
        var (means, variances, weights) = ReconstructParameters(data, gmmResult, k, d, n);

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
        float[][] data, in GmmResult gmmResult, int k, int d, int n)
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
                Array.Fill(variances[j], VarianceFloor);
                continue;
            }

            weights[j] = nk / n;
            ReconstructMeansForComponent(data, gmmResult.Responsibilities, means[j], n, d, j, nk);
            ReconstructVariancesForComponent(data, gmmResult.Responsibilities, means[j], variances[j], n, d, j, nk);
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
        int n, int d, int j, double nk)
    {
        for (int di = 0; di < d; di++)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double diff = data[i][di] - mean[di];
                sum += responsibilities[i][j] * diff * diff;
            }

            variance[di] = System.Math.Max(sum / nk, VarianceFloor);
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
