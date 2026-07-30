using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Smoke test over a REAL ONNX embedding model. Skipped unless the
/// <c>RAGNET_ONNX_EMBED_MODEL</c> and <c>RAGNET_ONNX_EMBED_VOCAB</c> environment variables point
/// to existing files (a token-level ONNX export plus its WordPiece vocab.txt) — the same gate
/// <c>LateChunkingIntegrationTests</c> uses, so one pair of variables covers both.
/// </summary>
public sealed class OnnxEmbeddingGeneratorSmokeTests
{
    [Fact]
    public async Task GenerateAsync_WithARealModel_ReturnsUnitLengthVectorsUnaffectedByBatching()
    {
        var modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_MODEL");
        var vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_VOCAB");
        Assert.SkipWhen(
            string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath) ||
            string.IsNullOrEmpty(vocabPath) || !File.Exists(vocabPath),
            "Set RAGNET_ONNX_EMBED_MODEL and RAGNET_ONNX_EMBED_VOCAB to existing model/vocab files to run this test.");

        var ct = TestContext.Current.CancellationToken;
        using var generator = new OnnxEmbeddingGenerator(new OnnxEmbeddingOptions
        {
            ModelPath = modelPath,
            TokenizerVocabPath = vocabPath,
        });

        const string ShortText = "Dense retrieval.";
        const string LongText =
            "Dense retrieval embeds a query and every passage into the same vector space and ranks " +
            "passages by cosine similarity, which is why the pooling and normalisation applied to " +
            "the model's token-level output decide the numbers a benchmark reports.";

        var alone = await generator.GenerateAsync([ShortText], cancellationToken: ct);
        var batched = await generator.GenerateAsync([ShortText, LongText], cancellationToken: ct);

        var dimension = alone[0].Vector.Length;
        Assert.True(dimension > 0, "embedding dimension must be positive");
        Assert.Equal(2, batched.Count);
        Assert.Equal(dimension, batched[0].Vector.Length);
        Assert.Equal(dimension, batched[1].Vector.Length);

        Assert.Equal(1.0, Norm(alone[0].Vector.Span), 1e-4);
        Assert.Equal(1.0, Norm(batched[0].Vector.Span), 1e-4);
        Assert.Equal(1.0, Norm(batched[1].Vector.Span), 1e-4);

        // The padding invariant against a real model. Tolerance rather than exact equality: the
        // masked padding changes the tensor SHAPE, so the kernels reduce in a different order and
        // the last bits of the mantissa legitimately differ. A pad-inclusive mean would be off by
        // orders of magnitude more than this.
        var cosine = Dot(alone[0].Vector.Span, batched[0].Vector.Span);
        Assert.Equal(1.0, cosine, 1e-3);
    }

    private static double Norm(ReadOnlySpan<float> vector)
    {
        double normSquared = 0;
        foreach (ref readonly var value in vector)
            normSquared += (double)value * value;

        return Math.Sqrt(normSquared);
    }

    private static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double dot = 0;
        for (var i = 0; i < left.Length; i++)
            dot += (double)left[i] * right[i];

        return dot;
    }
}
