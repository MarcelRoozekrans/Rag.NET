using System.Numerics;

namespace Rag.NET.PostRetrieval;

/// <summary>
/// The vector arithmetic behind dense scoring: a hardware-accelerated dot product, and cosine
/// similarity built on it.
/// <para>
/// <b>Vectorised with the in-box <see cref="Vector{T}"/> rather than TensorPrimitives.</b>
/// <c>System.Numerics.Tensors</c> is pinned in this repository but referenced only by
/// <c>Rag.NET.Evaluation.Ragas</c>; taking it here would add a dependency to <c>Rag.NET</c>
/// itself, the package almost every consumer installs. <see cref="Vector{T}"/> ships with the
/// runtime, widens to whatever the JIT finds at startup, and costs nothing in the nuspec.
/// </para>
/// <para>
/// <b>Summation order changes, so scores move in the last bits.</b> A vectorised reduction keeps
/// several partial sums and folds them at the end instead of accumulating strictly left to right.
/// The result is usually <i>more</i> accurate — shorter dependency chains accumulate less rounding
/// error — but it is not bit-identical to the scalar loop this replaced, and retrieval turns
/// scores into an ordering. Two chunks whose similarity differs in the seventh decimal can
/// therefore swap rank. That is why this change was verified against the pinned BEIR figures
/// rather than against a unit test asserting a float.
/// </para>
/// </summary>
internal static class EmbeddingMath
{
    /// <summary>Cosine similarity, computing both norms — for callers holding neither.</summary>
    /// <param name="a">One vector.</param>
    /// <param name="b">The other; a differing length scores 0.</param>
    /// <returns>Cosine similarity, or 0 when either vector has no magnitude.</returns>
    internal static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;
        if (spanA.Length != spanB.Length) return 0f;

        return CosineSimilarity(spanA, Norm(spanA), spanB, Norm(spanB));
    }

    /// <summary>
    /// Cosine similarity from norms the caller already holds — the scan form.
    /// <para>
    /// A linear scan recomputes both norms per candidate although neither changes: the query's is
    /// fixed for the whole scan and each stored vector's is fixed for the lifetime of the entry.
    /// Hoisting them leaves one multiply-accumulate chain where there were three, before any
    /// vectorisation.
    /// </para>
    /// </summary>
    /// <param name="a">One vector.</param>
    /// <param name="normA">Its Euclidean norm, as <see cref="Norm"/> returns it.</param>
    /// <param name="b">The other; a differing length scores 0.</param>
    /// <param name="normB">Its Euclidean norm.</param>
    /// <returns>Cosine similarity, or 0 when either norm is 0.</returns>
    internal static float CosineSimilarity(
        ReadOnlySpan<float> a, float normA, ReadOnlySpan<float> b, float normB)
    {
        if (a.Length != b.Length) return 0f;

        var denominator = normA * normB;
        return denominator == 0f ? 0f : Dot(a, b) / denominator;
    }

    /// <summary>The Euclidean norm — the square root of the vector's dot product with itself.</summary>
    /// <param name="vector">The vector to measure.</param>
    /// <returns>Its magnitude.</returns>
    internal static float Norm(ReadOnlySpan<float> vector) => MathF.Sqrt(Dot(vector, vector));

    /// <summary>Dot product, one <see cref="Vector{T}"/> wide at a time where the hardware allows.</summary>
    /// <param name="a">One vector.</param>
    /// <param name="b">The other, of the same length.</param>
    /// <returns>The dot product.</returns>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0f;
        var i = 0;

        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var accumulator = Vector<float>.Zero;
            var lastBlock = a.Length - Vector<float>.Count;
            for (; i <= lastBlock; i += Vector<float>.Count)
            {
                accumulator += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
            }

            sum = Vector.Sum(accumulator);
        }

        // The tail: 384 divides every current vector width, but the length is the caller's and a
        // model with an odd dimension must not silently score on a truncated vector.
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
