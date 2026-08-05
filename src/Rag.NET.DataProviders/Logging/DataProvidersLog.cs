using Microsoft.Extensions.Logging;

namespace Rag.NET.DataProviders.Logging;

internal static partial class DataProvidersLog
{
    [LoggerMessage(EventId = 1549259873, EventName = "ingestion_job_failed", Level = LogLevel.Warning, Message = "Ingestion job for document {DocumentId} failed: {Error}; continuing with next job")]
    internal static partial void IngestionJobFailed(ILogger logger, string documentId, string error);

    [LoggerMessage(EventId = 2131570182, EventName = "ingestion_job_threw", Level = LogLevel.Warning, Message = "Ingestion job for document {DocumentId} threw; continuing with next job")]
    internal static partial void IngestionJobThrew(ILogger logger, string documentId, Exception exception);

    [LoggerMessage(EventId = 1686355747, EventName = "polling_cycle_completed", Level = LogLevel.Information, Message = "Polling cycle for provider {ProviderId} completed: {Ingested} ingested, {Skipped} skipped, {Deleted} deleted, {ErrorCount} error(s)")]
    internal static partial void PollingCycleCompleted(ILogger logger, string providerId, int ingested, int skipped, int deleted, int errorCount);

    [LoggerMessage(EventId = 1955668817, EventName = "polling_cycle_failed", Level = LogLevel.Warning, Message = "Polling cycle for provider {ProviderId} failed; retrying next cycle")]
    internal static partial void PollingCycleFailed(ILogger logger, string providerId, Exception exception);
}
