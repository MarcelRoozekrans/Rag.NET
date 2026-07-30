using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Pins <see cref="OnnxEmbeddingGenerator"/>'s mean pooling and L2 normalisation on synthetic
/// tensors — no ONNX model, no vocabulary, no environment. These are the load-bearing numeric
/// details: including padding in the mean is the classic error, and it shifts every downstream
/// retrieval number silently rather than failing.
/// </summary>
public sealed class OnnxEmbeddingPoolingTests
{
    [Fact]
    public void MeanPool_MaskedRows_AreExcludedFromBothTheSumAndTheDivisor()
    {
        // Three token rows of dimension 2; the third is padding carrying an obvious outlier.
        // Correct: (1+3)/2 = 2 and (2+4)/2 = 3. Including the pad row would give 34.67/35.33,
        // and dividing by 3 while summing only two rows would give 1.33/2.
        float[] hidden = [1f, 2f, 3f, 4f, 100f, 100f];
        long[] mask = [1, 1, 0];

        var pooled = OnnxEmbeddingGenerator.MeanPool(hidden, mask, dimension: 2);

        Assert.Equal([2f, 3f], pooled);
    }

    [Fact]
    public void MeanPool_NoPadding_AveragesEveryRow()
    {
        float[] hidden = [1f, 2f, 3f, 4f, 5f, 6f];
        long[] mask = [1, 1, 1];

        var pooled = OnnxEmbeddingGenerator.MeanPool(hidden, mask, dimension: 2);

        Assert.Equal([3f, 4f], pooled);
    }

    [Fact]
    public void MeanPool_SamePrefixWithAndWithoutPadding_ProducesTheSameVector()
    {
        // The padding invariant, stated directly on the pooling function: the same real rows must
        // pool to the same vector however many pad rows follow them, and whatever those rows hold.
        float[] unpadded = [1f, 2f, 3f, 4f];
        float[] padded = [1f, 2f, 3f, 4f, -7f, 9999f, 0.5f, -0.5f];

        var fromUnpadded = OnnxEmbeddingGenerator.MeanPool(unpadded, [1, 1], dimension: 2);
        var fromPadded = OnnxEmbeddingGenerator.MeanPool(padded, [1, 1, 0, 0], dimension: 2);

        Assert.Equal(fromUnpadded, fromPadded);
    }

    [Fact]
    public void MeanPool_EveryRowMasked_ThrowsInsteadOfDividingByZero()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OnnxEmbeddingGenerator.MeanPool([1f, 2f], [0], dimension: 2));

        Assert.Contains("padding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MeanPool_HiddenLengthDoesNotMatchTheMask_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OnnxEmbeddingGenerator.MeanPool([1f, 2f, 3f], [1, 1], dimension: 2));

        Assert.Contains("2 token rows", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MeanPool_NonPositiveDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OnnxEmbeddingGenerator.MeanPool([1f, 2f], [1, 1], dimension: 0));
    }

    [Fact]
    public void L2NormalizeInPlace_ScalesToUnitLength()
    {
        float[] vector = [3f, 4f];

        var scaled = OnnxEmbeddingGenerator.L2NormalizeInPlace(vector);

        Assert.True(scaled);
        Assert.Equal([0.6f, 0.8f], vector);
        Assert.Equal(1.0, Norm(vector), 1e-6);
    }

    [Fact]
    public void L2NormalizeInPlace_NegativeComponents_KeepsDirectionAndReachesUnitLength()
    {
        float[] vector = [-1f, 2f, -2f];

        Assert.True(OnnxEmbeddingGenerator.L2NormalizeInPlace(vector));

        Assert.Equal(1.0, Norm(vector), 1e-6);
        Assert.True(vector[0] < 0 && vector[1] > 0 && vector[2] < 0, "signs must be preserved");
    }

    [Fact]
    public void L2NormalizeInPlace_ZeroVector_IsLeftAloneRatherThanDividedByZero()
    {
        float[] vector = [0f, 0f, 0f];

        var scaled = OnnxEmbeddingGenerator.L2NormalizeInPlace(vector);

        Assert.False(scaled);
        Assert.Equal([0f, 0f, 0f], vector);
        Assert.All(vector, value => Assert.False(float.IsNaN(value), "a zero vector must not become NaN"));
    }

    [Fact]
    public void L2NormalizeInPlace_AlreadyUnitLength_StaysUnitLength()
    {
        float[] vector = [1f, 0f, 0f];

        Assert.True(OnnxEmbeddingGenerator.L2NormalizeInPlace(vector));

        Assert.Equal([1f, 0f, 0f], vector);
    }

    [Fact]
    public void PoolThenNormalize_ExcludingPadding_ProducesAUnitVector()
    {
        var pooled = OnnxEmbeddingGenerator.MeanPool([1f, 2f, 3f, 4f, 500f, 500f], [1, 1, 0], dimension: 2);

        Assert.True(OnnxEmbeddingGenerator.L2NormalizeInPlace(pooled));

        Assert.Equal(1.0, Norm(pooled), 1e-6);
        // 2/sqrt(13), 3/sqrt(13) — the padding rows are nowhere in this answer.
        Assert.Equal(2.0 / Math.Sqrt(13), pooled[0], 1e-6);
        Assert.Equal(3.0 / Math.Sqrt(13), pooled[1], 1e-6);
    }

    [Fact]
    public void ValidateOutputShape_BatchedTokenLevelShape_Passes()
    {
        OnnxEmbeddingGenerator.ValidateOutputShape(
            [4, 12, 384], expectedBatchSize: 4, expectedSequenceLength: 12, "last_hidden_state");
    }

    [Fact]
    public void ValidateOutputShape_PooledExport_ThrowsNamingShapeAndOutput()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxEmbeddingGenerator.ValidateOutputShape(
                [4, 384], expectedBatchSize: 4, expectedSequenceLength: 12, "sentence_embedding"));

        Assert.Contains("sentence_embedding", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[4, 384]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("pooled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OnnxEmbeddingOptions.OutputName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOutputShape_WrongBatchSize_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxEmbeddingGenerator.ValidateOutputShape(
                [1, 12, 384], expectedBatchSize: 4, expectedSequenceLength: 12, "last_hidden_state"));

        Assert.Contains("[1, 12, 384]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOutputShape_WrongSequenceLength_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxEmbeddingGenerator.ValidateOutputShape(
                [4, 10, 384], expectedBatchSize: 4, expectedSequenceLength: 12, "last_hidden_state"));

        Assert.Contains("[4, 10, 384]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("12", ex.Message, StringComparison.Ordinal);
    }

    private static double Norm(float[] vector)
    {
        double normSquared = 0;
        foreach (ref readonly var value in vector.AsSpan())
            normSquared += (double)value * value;

        return Math.Sqrt(normSquared);
    }
}
