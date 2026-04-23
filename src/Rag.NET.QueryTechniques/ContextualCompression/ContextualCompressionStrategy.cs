namespace Rag.NET.QueryTechniques.ContextualCompression;

public enum ContextualCompressionStrategy
{
    /// <summary>Embedding-similarity based, no LLM calls. Default.</summary>
    Extractive,

    /// <summary>Per-chunk LLM rewrite in parallel.</summary>
    Abstractive,
}
