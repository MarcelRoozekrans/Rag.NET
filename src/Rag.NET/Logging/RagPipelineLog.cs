using Microsoft.Extensions.Logging;

namespace Rag.NET.Logging;

internal static partial class RagPipelineLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Ingesting document {DocumentId} ({ContentType})")]
    internal static partial void IngestStarted(ILogger logger, string documentId, string? contentType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingested document {DocumentId}: {ChunksStored} chunk(s) stored")]
    internal static partial void IngestCompleted(ILogger logger, string documentId, int chunksStored);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to ingest document {DocumentId}")]
    internal static partial void IngestFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retrieving chunks (TopK={TopK})")]
    internal static partial void RetrieveStarted(ILogger logger, int topK);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retrieved {ResultCount} chunk(s)")]
    internal static partial void RetrieveCompleted(ILogger logger, int resultCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query expansion failed for query '{Query}', falling back to single-query retrieval")]
    internal static partial void QueryExpansionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query retrieval failed for query '{Query}', skipping")]
    internal static partial void QueryRetrievalFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asking with query (TopK={TopK})")]
    internal static partial void AskStarted(ILogger logger, int topK);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranking failed for query '{Query}', returning results without reranking")]
    internal static partial void RerankingFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reranked {CandidateCount} candidates to {ResultCount} result(s)")]
    internal static partial void RerankingCompleted(ILogger logger, int candidateCount, int resultCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HyDE generation failed for query '{Query}', falling back to original query embedding")]
    internal static partial void HydeGenerationFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Embedding cache miss for query '{Query}'")]
    internal static partial void EmbeddingCacheMiss(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Result cache miss for query '{Query}'")]
    internal static partial void ResultCacheMiss(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding cache operation failed for query '{Query}'")]
    internal static partial void EmbeddingCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Result cache operation failed for query '{Query}'")]
    internal static partial void ResultCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Parent document retrieved for query '{Query}': {ChildCount} children -> {ParentCount} parents")]
    internal static partial void ParentDocumentRetrieved(ILogger logger, string query, int childCount, int parentCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parent document lookup failed for query '{Query}', returning child chunks")]
    internal static partial void ParentDocumentFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HyDE generated {Length}-char hypothetical document for query '{Query}'")]
    internal static partial void HydeDocumentGenerated(ILogger logger, string query, int length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Query expansion produced {VariantCount} variant(s) for query '{Query}'")]
    internal static partial void QueryExpansionCompleted(ILogger logger, string query, int variantCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Vector store search ({SearchMode}): {ResultCount} result(s) returned")]
    internal static partial void VectorStoreSearchCompleted(ILogger logger, string searchMode, int resultCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Redundancy filter: kept {OutputCount} of {InputCount} result(s)")]
    internal static partial void RedundancyFilterCompleted(ILogger logger, int inputCount, int outputCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redundancy filtering failed for query '{Query}', returning unfiltered results")]
    internal static partial void RedundancyFilteringFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MMR selection completed: {CandidateCount} candidates -> {ResultCount} result(s)")]
    internal static partial void MmrSelectionCompleted(ILogger logger, int candidateCount, int resultCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MMR selection failed for query '{Query}', returning candidates in original order")]
    internal static partial void MmrSelectionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MmrCandidateCount ({CandidateCount}) is less than TopK ({TopK}); MMR may return fewer results than requested")]
    internal static partial void MmrCandidateCountLessThanTopK(ILogger logger, int candidateCount, int topK);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM metadata extraction produced {TagCount} tag(s) for chunk {ChunkIndex}")]
    internal static partial void MetadataExtractionCompleted(ILogger logger, int tagCount, int chunkIndex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM metadata extraction failed for chunk {ChunkIndex}, skipping: {Error}")]
    internal static partial void MetadataExtractionFailed(ILogger logger, int chunkIndex, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Self-query produced {FilterCount} filter(s) for query '{Query}'")]
    internal static partial void SelfQueryCompleted(ILogger logger, string query, int filterCount);

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
}
