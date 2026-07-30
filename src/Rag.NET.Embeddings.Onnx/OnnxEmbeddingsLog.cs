using Microsoft.Extensions.Logging;

namespace Rag.NET.Embeddings.Onnx;

internal static partial class OnnxEmbeddingsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "SPLADE model logits dimension ({ModelVocabularySize}) does not match the tokenizer vocabulary size ({TokenizerVocabularySize}) from '{VocabPath}'; verify the model and vocab.txt belong together (some exports pad the vocabulary dimension, in which case this is harmless)")]
    internal static partial void SpladeVocabularySizeMismatch(
        ILogger logger, int modelVocabularySize, int tokenizerVocabularySize, string vocabPath);

    // Parameters stay in the same order as the placeholders: when they diverge, the LoggerMessage
    // generator emits a state type whose GetEnumerator returns a reference type, which HLQ006
    // rejects — and warnings are errors here.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The configured BatchSize ({ConfiguredBatchSize}) is reduced to 1 because the ONNX embedding model '{ModelPath}' declares no attention_mask input: without it the model would attend to the padding a batch requires, which no pooling choice can repair. Throughput will be lower; use an export that declares attention_mask to batch.")]
    internal static partial void BatchingDisabledWithoutAttentionMask(
        ILogger logger, int configuredBatchSize, string modelPath);
}
