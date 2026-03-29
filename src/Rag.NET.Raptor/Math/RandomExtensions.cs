namespace Rag.NET.Raptor.Math;

/// <summary>
/// Extension methods for <see cref="Random"/> to support statistical sampling.
/// </summary>
internal static class RandomExtensions
{
    /// <summary>
    /// Generates a normally distributed random number using the Box-Muller transform.
    /// </summary>
    internal static double NextGaussian(this Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Sin(2.0 * System.Math.PI * u2);
    }
}
