using Microsoft.Extensions.Logging;

namespace Rag.NET.Logging;

internal static partial class RagPipelineLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to ingest document {DocumentId}")]
    internal static partial void IngestFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query expansion failed for query '{Query}', falling back to single-query retrieval")]
    internal static partial void QueryExpansionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query retrieval failed for query '{Query}', skipping")]
    internal static partial void QueryRetrievalFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranking failed for query '{Query}', returning results without reranking")]
    internal static partial void RerankingFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HyDE generation failed for query '{Query}', falling back to original query embedding")]
    internal static partial void HydeGenerationFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "HyDE multi-hypothesis averaging unavailable ({Reason}) for query '{Query}'; falling back to the single-document or plain-query path")]
    internal static partial void HydeAveragingUnavailable(ILogger logger, string reason, string query);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HyDE generated {Survived} of {Requested} requested hypotheses for query '{Query}'")]
    internal static partial void HydePartialHypotheses(ILogger logger, int survived, int requested, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding cache operation failed for query '{Query}'")]
    internal static partial void EmbeddingCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Result cache operation failed for query '{Query}'")]
    internal static partial void ResultCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parent document lookup failed for query '{Query}', returning child chunks")]
    internal static partial void ParentDocumentFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redundancy filtering failed for query '{Query}', returning unfiltered results")]
    internal static partial void RedundancyFilteringFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MMR selection failed for query '{Query}', returning candidates in original order")]
    internal static partial void MmrSelectionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MmrCandidateCount ({CandidateCount}) is less than TopK ({TopK}); MMR may return fewer results than requested")]
    internal static partial void MmrCandidateCountLessThanTopK(ILogger logger, int candidateCount, int topK);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM metadata extraction produced {TagCount} tag(s) for chunk {ChunkIndex}")]
    internal static partial void MetadataExtractionCompleted(ILogger logger, int tagCount, int chunkIndex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM metadata extraction failed for chunk {ChunkIndex}, skipping: {Error}")]
    internal static partial void MetadataExtractionFailed(ILogger logger, int chunkIndex, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Self-query failed for query '{Query}', proceeding without filter: {Error}")]
    internal static partial void SelfQueryFailed(ILogger logger, string query, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Map-reduce map call failed for document '{DocumentId}', treating as not found")]
    internal static partial void MapReduceMapFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refine call failed for document '{DocumentId}', preserving previous answer")]
    internal static partial void RefineStepFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EnsembleBehavior: BM25 search failed; falling back to dense-only results")]
    internal static partial void EnsembleBm25Failed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ConversationMemoryPipeline: summary LLM call failed; returning trimmed history without summary")]
    internal static partial void ConversationSummaryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Adaptive retrieval classification failed for query '{Query}', defaulting to complex")]
    internal static partial void AdaptiveClassificationFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CRAG web search failed for query '{Query}', returning original vector results")]
    internal static partial void CragWebSearchFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CRAG LLM relevance scoring failed for query '{Query}', falling back to heuristic scoring")]
    internal static partial void CragLlmScoringFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Contextual compression failed for query '{Query}'; returning uncompressed results.")]
    internal static partial void ContextualCompressionFailed(ILogger logger, string query, Exception exception);
}
