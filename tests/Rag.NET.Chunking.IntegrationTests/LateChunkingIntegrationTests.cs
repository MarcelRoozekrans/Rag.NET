using Rag.NET.Chunking;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Chunking.IntegrationTests;

/// <summary>
/// End-to-end late-chunking smoke over a REAL ONNX token-embedding model. Skipped unless the
/// <c>RAGNET_ONNX_EMBED_MODEL</c> and <c>RAGNET_ONNX_EMBED_VOCAB</c> environment variables
/// point to existing files (a token-level ONNX export, e.g. jina-embeddings-v2-style, plus
/// its WordPiece vocab.txt).
/// </summary>
public sealed class LateChunkingIntegrationTests
{
    private const string TwoParagraphText =
        "Late chunking embeds the full document before splitting it, so every token vector " +
        "carries whole-document context. The embedding model produces one vector per token, " +
        "and chunk embeddings are derived afterwards by pooling those vectors over windows.\n\n" +
        "This second paragraph refers back to the technique above. Because the token vectors " +
        "were computed with full attention over both paragraphs, this reference keeps its " +
        "meaning even after the text is cut into separate chunks.";

    [Fact]
    public async Task LateChunking_WithOnnxGenerator_ProducesNormalizedEmbeddings()
    {
        var modelPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_MODEL");
        var vocabPath = Environment.GetEnvironmentVariable("RAGNET_ONNX_EMBED_VOCAB");
        Assert.SkipWhen(
            string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath) ||
            string.IsNullOrEmpty(vocabPath) || !File.Exists(vocabPath),
            "Set RAGNET_ONNX_EMBED_MODEL and RAGNET_ONNX_EMBED_VOCAB to existing model/vocab files to run this test.");

        var ct = TestContext.Current.CancellationToken;
        using var generator = new OnnxTokenEmbeddingGenerator(new OnnxTokenEmbeddingOptions
        {
            ModelPath = modelPath,
            TokenizerVocabPath = vocabPath,
        });
        var strategy = new LateChunkingStrategy(
            generator,
            new LateChunkingOptions { WindowSizeTokens = 32, OverlapTokens = 4 });
        var section = new DocumentSection
        {
            DocumentId = new DocumentId("late-chunking-smoke"),
            Text = TwoParagraphText,
        };

        var chunks = await strategy.ChunkAsync(section, new ChunkingOptions(), ct).ToListAsync(ct);

        Assert.True(chunks.Count > 1, $"expected multiple chunks, got {chunks.Count}");
        Assert.All(chunks, c => Assert.NotNull(c.Embedding));

        var dimension = chunks[0].Embedding!.Value.Length;
        Assert.True(dimension > 0, "embedding dimension must be positive");
        Assert.All(chunks, c =>
        {
            Assert.Equal(dimension, c.Embedding!.Value.Length);

            // LateChunkingStrategy L2-normalizes pooled window vectors.
            double normSquared = 0;
            foreach (ref readonly var value in c.Embedding!.Value.Span)
                normSquared += (double)value * value;
            Assert.Equal(1.0, Math.Sqrt(normSquared), 1e-3);
        });
    }
}
