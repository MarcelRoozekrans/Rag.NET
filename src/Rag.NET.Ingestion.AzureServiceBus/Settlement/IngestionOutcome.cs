namespace Rag.NET.Ingestion.AzureServiceBus.Settlement;

/// <summary>
/// What happened to one Service Bus message once the trigger tried to ingest it. Mapped to a
/// broker settlement by <see cref="SettlePolicy.For(IngestionOutcome)"/>.
/// </summary>
public enum IngestionOutcome
{
    /// <summary>The document was ingested. The message is done.</summary>
    Succeeded,

    /// <summary>
    /// The attempt failed for a reason that may not recur — I/O, a timeout, throttling, a
    /// storage or transport fault. Redelivery is worth trying, and the broker's
    /// <c>MaxDeliveryCount</c> is the backstop that eventually dead-letters a message that
    /// keeps failing this way.
    /// </summary>
    TransientFailure,

    /// <summary>
    /// The attempt failed for a reason redelivery cannot fix — an unparseable body, a missing
    /// required field, no parser for the content. Retrying burns the delivery budget for
    /// nothing, so the message goes straight to the dead-letter queue with a reason.
    /// </summary>
    PermanentFailure,

    /// <summary>
    /// The host is shutting down and the attempt was abandoned mid-flight. This is not a
    /// failure of the message and must not be settled as one: leaving it unsettled lets the
    /// lock expire and the broker redeliver it, which is exactly the at-least-once behaviour
    /// the transport exists to provide.
    /// </summary>
    ShutdownAborted,
}
