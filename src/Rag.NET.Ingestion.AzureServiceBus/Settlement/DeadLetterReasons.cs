namespace Rag.NET.Ingestion.AzureServiceBus.Settlement;

/// <summary>
/// The fixed set of dead-letter reasons this trigger writes to
/// <c>ServiceBusReceivedMessage.DeadLetterReason</c>. Fixed rather than free-form because the
/// reason is what an operator filters and alerts on; the variable detail goes in the
/// dead-letter <i>description</i> instead.
/// </summary>
public static class DeadLetterReasons
{
    /// <summary>The body is not JSON, is empty, or is not a JSON object.</summary>
    public const string MalformedPayload = "MalformedPayload";

    /// <summary>
    /// The body parsed but does not satisfy the contract — a missing or empty
    /// <c>documentId</c> or <c>content</c>, or a <c>metadata</c> value that is not a flat
    /// string map.
    /// </summary>
    public const string MissingRequiredField = "MissingRequiredField";

    /// <summary>
    /// The message carries a <c>SessionId</c> that disagrees with the body's
    /// <c>documentId</c>. Sessions exist here to give per-document FIFO, so a session that
    /// does not name the document it orders is a producer bug, not a payload the trigger can
    /// guess its way through.
    /// </summary>
    public const string SessionDocumentMismatch = "SessionDocumentMismatch";

    /// <summary>
    /// Ingestion itself rejected the document in a way redelivery cannot fix — no parser for
    /// the content type, or a validation failure.
    /// </summary>
    public const string IngestionRejected = "IngestionRejected";
}
