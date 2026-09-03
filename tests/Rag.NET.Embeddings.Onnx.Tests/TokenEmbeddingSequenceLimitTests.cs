using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Holds <see cref="OnnxTokenEmbeddingOptions.MaxTokens"/> to the sequence limit its sibling in the
/// same package already assumes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins, found 2026-09-03 while building a late-chunking benchmark cell.</b>
/// <see cref="OnnxEmbeddingOptions.MaxTokens"/> defaulted to 256 and
/// <see cref="OnnxTokenEmbeddingOptions.MaxTokens"/> to 8192 — the same option name, in the same
/// package, for the same model, a factor of 32 apart. The token generator's documentation calls it
/// "the model's sequence limit" and promises that longer inputs are "windowed internally and
/// stitched back together", so the value is what decides whether windowing happens at all. At 8192
/// it never happened: <c>all-MiniLM-L6-v2</c> was handed sequences it cannot embed and ONNX Runtime
/// threw at the position-embedding node.
/// </para>
/// <para>
/// <b>Why that reached a user rather than a crash report.</b> <c>LateChunkingStrategy</c> catches
/// every generator exception and falls back to unembedded chunks, and <c>EmbeddingBehavior</c> then
/// backfills those with ordinary embeddings — correct behaviour in isolation, because one awkward
/// section should not fail a document. Together they mean a caller who configures
/// <c>UseLateChunking()</c> with this package's own model gets <b>ordinary embeddings on exactly the
/// documents long enough to need late chunking</b>, with no error and no log unless they passed a
/// logger. Measured on SciFact before the fix: 1,401 of 9,506 units carried no late-chunked
/// embedding, and 393 of the first 400 document failures were this cause.
/// </para>
/// <para>
/// <b>What this test guards is the relationship, not the number.</b> Pinning 256 alone would pass
/// while its sibling moved to some other value and the two drifted apart again in the other
/// direction. The invariant is that the per-pass limit of the token generator is not larger than
/// the limit the pooled generator in the same package already applies to the same model — because
/// the two read the same <c>RAGNET_ONNX_EMBED_MODEL</c>, and a limit one of them exceeds is a limit
/// the model does not have.
/// </para>
/// </remarks>
public sealed class TokenEmbeddingSequenceLimitTests
{
    [Fact]
    public void TokenGeneratorsDefaultPerPassLimit_DoesNotExceedThePooledGenerators()
    {
        var pooled = new OnnxEmbeddingOptions
        {
            ModelPath = "unused/model.onnx",
            TokenizerVocabPath = "unused/vocab.txt",
        };

        var token = new OnnxTokenEmbeddingOptions
        {
            ModelPath = "unused/model.onnx",
            TokenizerVocabPath = "unused/vocab.txt",
        };

        Assert.True(
            token.MaxTokens <= pooled.MaxTokens,
            $"{nameof(OnnxTokenEmbeddingOptions)}.{nameof(OnnxTokenEmbeddingOptions.MaxTokens)} is " +
            $"{token.MaxTokens} where {nameof(OnnxEmbeddingOptions)}." +
            $"{nameof(OnnxEmbeddingOptions.MaxTokens)} is {pooled.MaxTokens}. Both read the same " +
            "model. A per-pass limit larger than the pooled generator's means windowing never " +
            "triggers, the model is handed a sequence it cannot embed, and late chunking falls back " +
            "to ordinary embeddings without saying so — which is what shipped until 2026-09-03.");
    }

    [Fact]
    public void TokenGeneratorsDefaultPerPassLimit_LeavesRoomForTheSpecialTokens()
    {
        // The generator subtracts the [CLS]/[SEP] positions from MaxTokens to get the content
        // budget. A default at or below that reservation would make the content budget zero or
        // negative, which is a configuration error shaped like a working one.
        var token = new OnnxTokenEmbeddingOptions
        {
            ModelPath = "unused/model.onnx",
            TokenizerVocabPath = "unused/vocab.txt",
        };

        Assert.True(token.MaxTokens > 2, $"MaxTokens ({token.MaxTokens}) must exceed the two special positions.");
    }
}
