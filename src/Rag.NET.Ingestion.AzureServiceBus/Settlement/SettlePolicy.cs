using System.Net;
using Rag.NET.Models;

namespace Rag.NET.Ingestion.AzureServiceBus.Settlement;

/// <summary>
/// The pure decision layer of the trigger: outcome to settlement, and ingestion error to
/// outcome. No broker, no I/O, no state — every branch is directly assertable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Extension methods, since #185.</b> The two operations used to be plain statics, and the one
/// real call site read <c>SettlePolicy.For(SettlePolicy.Classify(error))</c> — nested statics, which
/// read inside-out. As <c>error.Classify().ToSettleAction()</c> it reads in evaluation order, which
/// is the order the pipeline actually applies them in.
/// </para>
/// <para>
/// <b><c>For</c> became <c>ToSettleAction</c> rather than keeping its name</b>, because <c>For</c>
/// is meaningless on a receiver: <c>outcome.For()</c> says nothing. This is the one conversion in
/// #185 that touches public API, so both originals remain as <c>[Obsolete]</c> forwarders rather
/// than being deleted.
/// </para>
/// <para>
/// <b>Null receivers still throw.</b> An extension method can be invoked on a null reference, so
/// <c>Classify</c>'s <c>ArgumentNullException.ThrowIfNull</c> guard is unchanged in behaviour — but
/// <c>null!.Classify()</c> does not compile, because extension lookup cannot bind against an
/// untyped <c>null</c> literal. The test therefore assigns <c>RagError error = null!;</c> first.
/// Same behaviour, different source, and worth stating because "the guard still passes" would
/// otherwise be asserted without anyone checking the call still binds.
/// </para>
/// </remarks>
public static class SettlePolicy
{
    /// <summary>Maps an ingestion outcome onto the broker settlement it deserves.</summary>
    /// <param name="outcome">What happened to the message.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is not a defined <see cref="IngestionOutcome"/>.
    /// </exception>
    public static SettleAction ToSettleAction(this IngestionOutcome outcome) => outcome switch
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
    public static IngestionOutcome Classify(this RagError error)
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

    /// <summary>Obsolete forwarder for the pre-#185 static form.</summary>
    /// <param name="outcome">What happened to the message.</param>
    /// <remarks>
    /// Kept because <see cref="SettlePolicy"/> is public and this is a rename as well as a
    /// conversion. Deleting it would break a caller for a readability change, which is not a trade
    /// this repository makes; the forwarder costs one line and says where to go.
    /// </remarks>
    [Obsolete("Use outcome.ToSettleAction(). 'For' is meaningless on a receiver — see issue #185.")]
    public static SettleAction For(IngestionOutcome outcome) => outcome.ToSettleAction();

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status >= 500;
}
