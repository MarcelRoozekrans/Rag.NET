using Microsoft.Extensions.Logging;

namespace Rag.NET.Embeddings.Onnx;

internal static partial class OnnxEmbeddingsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "SPLADE model logits dimension ({ModelVocabularySize}) does not match the tokenizer vocabulary size ({TokenizerVocabularySize}) from '{VocabPath}'; verify the model and vocab.txt belong together (some exports pad the vocabulary dimension, in which case this is harmless)")]
    internal static partial void SpladeVocabularySizeMismatch(
        ILogger logger, int modelVocabularySize, int tokenizerVocabularySize, string vocabPath);
}
