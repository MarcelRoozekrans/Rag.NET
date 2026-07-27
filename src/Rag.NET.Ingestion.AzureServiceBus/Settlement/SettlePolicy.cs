using System.Net;
using Rag.NET.Models;

namespace Rag.NET.Ingestion.AzureServiceBus.Settlement;

/// <summary>
/// The pure decision layer of the trigger: outcome to settlement, and ingestion error to
/// outcome. No broker, no I/O, no state — every branch is directly assertable.
/// </summary>
public static class SettlePolicy
{
    /// <summary>Maps an ingestion outcome onto the broker settlement it deserves.</summary>
    /// <param name="outcome">What happened to the message.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined <see cref="IngestionOutcome"/>.
    /// </exception>
    public static SettleAction For(IngestionOutcome outcome) => outcome switch
    {
        IngestionOutcome.Succeeded => SettleAction.Complete,
        IngestionOutcome.TransientFailure => SettleAction.Abandon,
        IngestionOutcome.PermanentFailure => SettleAction.DeadLetter,
        IngestionOutcome.ShutdownAborted => SettleAction.Leave,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
            "Not a defined IngestionOutcome value."),
    };

    /// <summary>
    /// Classifies a failed ingestion <see cref="RagError"/>. The union carries real signal:
    /// a missing parser or a validation failure is the same on every redelivery, while a
    /// storage or transport fault is exactly the kind of thing a retry fixes.
    /// </summary>
    /// <param name="error">The error the ingestor returned.</param>
    public static IngestionOutcome Classify(RagError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error switch
        {
            RagError.ValidationFailed => IngestionOutcome.PermanentFailure,
            RagError.NoParserFound => IngestionOutcome.PermanentFailure,
            RagError.NonSeekableStream => IngestionOutcome.PermanentFailure,
            RagError.HttpFailed http => IsRetryable(http.StatusCode)
                ? IngestionOutcome.TransientFailure
                : IngestionOutcome.PermanentFailure,
            // StorageFailed, TransportFailed, and any error added later: assume transient.
            // Getting this wrong in the transient direction costs redeliveries that the
            // broker's MaxDeliveryCount eventually converts into a dead-letter anyway;
            // getting it wrong in the permanent direction discards a document that would
            // have ingested fine on the next attempt.
            _ => IngestionOutcome.TransientFailure,
        };
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;
}
