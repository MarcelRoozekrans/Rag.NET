using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Hand-computed coverage of the pure SPLADE pooling math:
/// <see cref="OnnxSpladeEncoder.PoolWindow"/>, <see cref="OnnxSpladeEncoder.MaxMergeInPlace"/>,
/// and <see cref="OnnxSpladeEncoder.PruneTopTerms"/>.
/// </summary>
public class SpladePoolingTests
{
    [Fact]
    public void PoolWindow_ReluZeroesNegatives_Log1pApplied_MaxOverTokens()
    {
        // vocab = 4, two token rows:
        //   t0: [ 1, -2, 0, 3 ]
        //   t1: [ 2, -1, 0, 1 ]
        // max over tokens: [2, -1, 0, 3] → ReLU → [2, 0, 0, 3] → log1p → [ln 3, 0, 0, ln 4]
        float[] logits = [1f, -2f, 0f, 3f, 2f, -1f, 0f, 1f];

        var weights = OnnxSpladeEncoder.PoolWindow(logits, vocabularySize: 4);

        Assert.Equal(4, weights.Length);
        Assert.Equal(MathF.Log(3f), weights[0], precision: 5);
        Assert.Equal(0f, weights[1]);
        Assert.Equal(0f, weights[2]);
        Assert.Equal(MathF.Log(4f), weights[3], precision: 5);
    }

    [Fact]
    public void PoolWindow_LengthNotMultipleOfVocab_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            OnnxSpladeEncoder.PoolWindow([1f, 2f, 3f], vocabularySize: 2));

        Assert.Contains("multiple", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaxMergeInPlace_TakesElementWiseMax()
    {
        float[] accumulator = [1f, 0f, 2f];

        OnnxSpladeEncoder.MaxMergeInPlace(accumulator, [0.5f, 3f, 1f]);

        Assert.Equal([1f, 3f, 2f], accumulator);
    }

    [Fact]
    public void MaxMergeInPlace_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            OnnxSpladeEncoder.MaxMergeInPlace([1f, 2f], [1f]));
    }

    [Fact]
    public void PruneTopTerms_KeepsLargestK_AscendingIndices()
    {
        float[] weights = [0f, 5f, 0f, 3f, 4f, 0f];

        var sparse = OnnxSpladeEncoder.PruneTopTerms(weights, topTerms: 2);

        Assert.Equal(2, sparse.Count);
        Assert.Equal([1, 4], sparse.Indices.ToArray());
        Assert.Equal([5f, 4f], sparse.Values.ToArray());
    }

    [Fact]
    public void PruneTopTerms_DropsZeros_EvenUnderBudget()
    {
        float[] weights = [0f, 1f, 0f];

        var sparse = OnnxSpladeEncoder.PruneTopTerms(weights, topTerms: 5);

        Assert.Equal(1, sparse.Count);
        Assert.Equal([1], sparse.Indices.ToArray());
        Assert.Equal([1f], sparse.Values.ToArray());
    }

    [Fact]
    public void PruneTopTerms_Ties_LowerTermIdsWin()
    {
        float[] weights = [2f, 2f, 2f];

        var sparse = OnnxSpladeEncoder.PruneTopTerms(weights, topTerms: 2);

        Assert.Equal([0, 1], sparse.Indices.ToArray());
    }

    [Fact]
    public void PruneTopTerms_AllZero_ReturnsEmpty()
    {
        var sparse = OnnxSpladeEncoder.PruneTopTerms([0f, 0f], topTerms: 2);

        Assert.Equal(0, sparse.Count);
    }

    [Fact]
    public void ResolveOutputName_PreferredMissingAmongMultiple_ThrowsNamingSpladeOptions()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxSpladeEncoder.ResolveOutputName(["last_hidden_state", "pooler_output"], "logits"));

        Assert.Contains("logits", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OnnxSpladeOptions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateOutputShape_PooledExport_ThrowsNamingShape()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OnnxSpladeEncoder.ValidateOutputShape([1, 768], expectedSequenceLength: 12, "logits"));

        Assert.Contains("[1, 768]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vocabulary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOutputShape_LogitsShape_Passes()
    {
        OnnxSpladeEncoder.ValidateOutputShape([1, 12, 30522], expectedSequenceLength: 12, "logits");
    }
}
