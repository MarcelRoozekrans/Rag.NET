using Microsoft.Extensions.Logging;

namespace Rag.NET.Logging;

internal static partial class RagPipelineLog
{
    [LoggerMessage(EventId = 1983936292, EventName = "ingest_failed", Level = LogLevel.Error, Message = "Failed to ingest document {DocumentId}")]
    internal static partial void IngestFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 240408873, EventName = "query_expansion_failed", Level = LogLevel.Warning, Message = "Query expansion failed for query '{Query}', falling back to single-query retrieval")]
    internal static partial void QueryExpansionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 532533048, EventName = "query_retrieval_failed", Level = LogLevel.Warning, Message = "Query retrieval failed for query '{Query}', skipping")]
    internal static partial void QueryRetrievalFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 967345512, EventName = "context_budget_dropped_chunks", Level = LogLevel.Information, Message = "Context budget dropped {Dropped} of {Total} retrieved chunks: {TotalTokens} tokens exceeded the {Budget}-token budget, {KeptTokens} kept")]
    internal static partial void ContextBudgetDroppedChunks(
        ILogger logger, int dropped, int total, int totalTokens, int budget, int keptTokens);

    [LoggerMessage(EventId = 175297165, EventName = "reranking_failed", Level = LogLevel.Warning, Message = "Reranking failed for query '{Query}', returning the unreranked results cut to TopK")]
    internal static partial void RerankingFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 331882407, EventName = "reranking_returned_fewer_than_requested", Level = LogLevel.Warning, Message = "Reranker {Reranker} returned {Returned} results for a TopK of {Requested}, so the answer is built from fewer chunks than requested; check the reranker's own result cap (e.g. CohereRerankerOptions.TopN)")]
    internal static partial void RerankingReturnedFewerThanRequested(
        ILogger logger, string reranker, int returned, int requested);

    [LoggerMessage(EventId = 846114106, EventName = "hyde_generation_failed", Level = LogLevel.Warning, Message = "HyDE generation failed for query '{Query}', falling back to original query embedding")]
    internal static partial void HydeGenerationFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 1440978751, EventName = "hyde_averaging_unavailable", Level = LogLevel.Information, Message = "HyDE multi-hypothesis averaging unavailable ({Reason}) for query '{Query}'; falling back to the single-document or plain-query path")]
    internal static partial void HydeAveragingUnavailable(ILogger logger, string reason, string query);

    [LoggerMessage(EventId = 894911740, EventName = "hyde_partial_hypotheses", Level = LogLevel.Debug, Message = "HyDE generated {Survived} of {Requested} requested hypotheses for query '{Query}'")]
    internal static partial void HydePartialHypotheses(ILogger logger, int survived, int requested, string query);

    [LoggerMessage(EventId = 1143233563, EventName = "embedding_cache_failed", Level = LogLevel.Warning, Message = "Embedding cache operation failed for query '{Query}'")]
    internal static partial void EmbeddingCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 1583015041, EventName = "result_cache_failed", Level = LogLevel.Warning, Message = "Result cache operation failed for query '{Query}'")]
    internal static partial void ResultCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 250055121, EventName = "parent_document_failed", Level = LogLevel.Warning, Message = "Parent document lookup failed for query '{Query}', returning child chunks")]
    internal static partial void ParentDocumentFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 1753402195, EventName = "redundancy_filtering_failed", Level = LogLevel.Warning, Message = "Redundancy filtering failed for query '{Query}', returning unfiltered results")]
    internal static partial void RedundancyFilteringFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 2142643646, EventName = "mmr_selection_failed", Level = LogLevel.Warning, Message = "MMR selection failed for query '{Query}', returning candidates in original order")]
    internal static partial void MmrSelectionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 410492513, EventName = "mmr_candidate_count_less_than_top_k", Level = LogLevel.Warning, Message = "MmrCandidateCount ({CandidateCount}) is less than TopK ({TopK}); MMR may return fewer results than requested")]
    internal static partial void MmrCandidateCountLessThanTopK(ILogger logger, int candidateCount, int topK);

    [LoggerMessage(EventId = 526312510, EventName = "metadata_extraction_completed", Level = LogLevel.Debug, Message = "LLM metadata extraction produced {TagCount} tag(s) for chunk {ChunkIndex}")]
    internal static partial void MetadataExtractionCompleted(ILogger logger, int tagCount, int chunkIndex);

    [LoggerMessage(EventId = 1251969072, EventName = "metadata_extraction_failed", Level = LogLevel.Warning, Message = "LLM metadata extraction failed for chunk {ChunkIndex}, skipping: {Error}")]
    internal static partial void MetadataExtractionFailed(ILogger logger, int chunkIndex, string error);

    [LoggerMessage(EventId = 649689236, EventName = "self_query_failed", Level = LogLevel.Warning, Message = "Self-query failed for query '{Query}', proceeding without filter: {Error}")]
    internal static partial void SelfQueryFailed(ILogger logger, string query, string error);

    [LoggerMessage(EventId = 1318312786, EventName = "map_reduce_map_failed", Level = LogLevel.Warning, Message = "Map-reduce map call failed for document '{DocumentId}', treating as not found")]
    internal static partial void MapReduceMapFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1606707285, EventName = "refine_step_failed", Level = LogLevel.Warning, Message = "Refine call failed for document '{DocumentId}', preserving previous answer")]
    internal static partial void RefineStepFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 73534105, EventName = "ensemble_bm25_failed", Level = LogLevel.Warning, Message = "EnsembleBehavior: BM25 search failed; falling back to dense-only results")]
    internal static partial void EnsembleBm25Failed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1302726483, EventName = "conversation_summary_failed", Level = LogLevel.Warning, Message = "ConversationMemoryPipeline: summary LLM call failed; returning trimmed history without summary")]
    internal static partial void ConversationSummaryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 86375506, EventName = "adaptive_classification_failed", Level = LogLevel.Warning, Message = "Adaptive retrieval classification failed for query '{Query}', defaulting to complex")]
    internal static partial void AdaptiveClassificationFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 1559230941, EventName = "crag_web_search_failed", Level = LogLevel.Warning, Message = "CRAG web search failed for query '{Query}', returning original vector results")]
    internal static partial void CragWebSearchFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 977539349, EventName = "crag_llm_scoring_failed", Level = LogLevel.Warning, Message = "CRAG LLM relevance scoring failed for query '{Query}', falling back to heuristic scoring")]
    internal static partial void CragLlmScoringFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 222311371, EventName = "contextual_compression_failed", Level = LogLevel.Warning, Message = "Contextual compression failed for query '{Query}'; returning uncompressed results.")]
    internal static partial void ContextualCompressionFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(EventId = 714883371, EventName = "federated_store_search_failed", Level = LogLevel.Warning, Message = "Federated vector store '{StoreName}' failed to serve the search; skipping it")]
    internal static partial void FederatedStoreSearchFailed(ILogger logger, string storeName, Exception exception);

    [LoggerMessage(EventId = 2036162545, EventName = "ensemble_sparse_failed", Level = LogLevel.Warning, Message = "EnsembleBehavior: sparse search failed; continuing with the remaining arms")]
    internal static partial void EnsembleSparseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 958903901, EventName = "sparse_embedding_failed", Level = LogLevel.Warning, Message = "Sparse embedding generation failed for document '{DocumentId}'; proceeding with dense-only storage")]
    internal static partial void SparseEmbeddingFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1162579367, EventName = "sparse_storage_failed", Level = LogLevel.Warning, Message = "Sparse vector storage failed for document '{DocumentId}'; dense vectors were stored")]
    internal static partial void SparseStorageFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1476922516, EventName = "embedding_version_stamp_failed", Level = LogLevel.Warning, Message = "Failed to stamp the embedding version for document '{DocumentId}'; ingestion succeeded, but re-indexing may miss or mis-report this document")]
    internal static partial void EmbeddingVersionStampFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 125720330, EventName = "embedding_version_identity_unresolvable", Level = LogLevel.Warning, Message = "An embedding version store is registered but the embedding model identity is unresolvable (the generator exposes no EmbeddingGeneratorMetadata with a model id and EmbeddingVersioningOptions.ModelId is not set); version stamping is disabled")]
    internal static partial void EmbeddingVersionIdentityUnresolvable(ILogger logger);

    [LoggerMessage(EventId = 94309004, EventName = "reindex_document_failed", Level = LogLevel.Warning, Message = "Re-indexing failed for document '{DocumentId}'; continuing with the remaining stale documents")]
    internal static partial void ReindexDocumentFailed(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1495423416, EventName = "cost_ledger_read_failed", Level = LogLevel.Warning, Message = "Cost ledger read failed; proceeding without budget enforcement for this call")]
    internal static partial void CostLedgerReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 203256243, EventName = "cost_ledger_record_failed", Level = LogLevel.Warning, Message = "Cost ledger write failed; the call succeeded but its usage was not recorded")]
    internal static partial void CostLedgerRecordFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1730415878, EventName = "ensemble_native_hybrid", Level = LogLevel.Debug, Message = "EnsembleBehavior: native hybrid search dispatched to {StoreName}")]
    internal static partial void EnsembleNativeHybrid(ILogger logger, string storeName);
}
