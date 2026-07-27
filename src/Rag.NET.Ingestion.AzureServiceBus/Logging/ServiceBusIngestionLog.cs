using Microsoft.Extensions.Logging;

namespace Rag.NET.Ingestion.AzureServiceBus.Logging;

/// <summary>
/// LoggerMessage source-generated logging for the Service Bus trigger.
/// </summary>
/// <remarks>
/// Every message lists its placeholders in the same order as the method's parameters, and must
/// keep doing so. When the two orders agree the generator emits a
/// <c>LoggerMessage.Define&lt;…&gt;</c> call; when they disagree it emits a private state struct
/// whose <c>GetEnumerator</c> returns a reference type, which trips HLQ006 — an error in this
/// repo. Reordering the words is free; a pragma would not be.
/// </remarks>
internal static partial class ServiceBusIngestionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus ingestion started on {EntityPath} (sessions: {SessionsEnabled})")]
    internal static partial void ProcessingStarted(ILogger logger, string entityPath, bool sessionsEnabled);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus ingestion stopped on {EntityPath}")]
    internal static partial void ProcessingStopped(ILogger logger, string entityPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{EntityPath}: ingested document {DocumentId}; completing message {MessageId}")]
    internal static partial void MessageCompleted(ILogger logger, string entityPath, string documentId, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EntityPath}: ingestion of message {MessageId} failed on delivery {DeliveryCount}; abandoning for redelivery: {Detail}")]
    internal static partial void MessageAbandoned(ILogger logger, string entityPath, string messageId, int deliveryCount, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EntityPath}: ingestion of message {MessageId} threw on delivery {DeliveryCount}; abandoning for redelivery")]
    internal static partial void MessageAbandonedAfterThrow(ILogger logger, string entityPath, string messageId, int deliveryCount, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "{EntityPath}: message {MessageId} dead-lettered as {Reason}: {Description}")]
    internal static partial void MessageDeadLettered(ILogger logger, string entityPath, string messageId, string reason, string? description);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EntityPath}: host is shutting down mid-ingestion of message {MessageId}; leaving it unsettled so the broker redelivers it")]
    internal static partial void MessageLeftForShutdown(ILogger logger, string entityPath, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EntityPath}: settling message {MessageId} as {Action} failed; the lock will expire and the broker will redeliver")]
    internal static partial void SettlementFailed(ILogger logger, string entityPath, string messageId, string action, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service Bus processor error on {EntityPath} ({ErrorSource}); the processor keeps running")]
    internal static partial void ProcessorError(ILogger logger, string entityPath, string errorSource, Exception exception);
}
