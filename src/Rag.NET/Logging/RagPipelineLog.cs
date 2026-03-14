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
}
