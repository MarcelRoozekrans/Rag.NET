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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Asking with query (TopK={TopK})")]
    internal static partial void AskStarted(ILogger logger, int topK);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reranking failed for query '{Query}', returning results without reranking")]
    internal static partial void RerankingFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reranked {CandidateCount} candidates to {ResultCount} result(s)")]
    internal static partial void RerankingCompleted(ILogger logger, int candidateCount, int resultCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HyDE generation failed for query '{Query}', falling back to original query embedding")]
    internal static partial void HydeGenerationFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Embedding cache hit for query '{Query}'")]
    internal static partial void EmbeddingCacheHit(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Result cache hit for query '{Query}'")]
    internal static partial void ResultCacheHit(ILogger logger, string query);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding cache operation failed for query '{Query}'")]
    internal static partial void EmbeddingCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Result cache operation failed for query '{Query}'")]
    internal static partial void ResultCacheFailed(ILogger logger, string query, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Parent document retrieved for query '{Query}': {ChildCount} children -> {ParentCount} parents")]
    internal static partial void ParentDocumentRetrieved(ILogger logger, string query, int childCount, int parentCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parent document lookup failed for query '{Query}', returning child chunks")]
    internal static partial void ParentDocumentFailed(ILogger logger, string query, Exception exception);
}
