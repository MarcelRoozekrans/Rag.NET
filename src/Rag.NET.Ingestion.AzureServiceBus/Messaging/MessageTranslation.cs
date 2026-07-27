using System.Diagnostics.CodeAnalysis;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Rag.NET.Models;

namespace Rag.NET.Ingestion.AzureServiceBus.Messaging;

/// <summary>
/// The result of turning one Service Bus message body into an <see cref="IngestionJob"/>:
/// either the job, or the dead-letter disposition explaining why the payload can never
/// succeed.
/// </summary>
/// <remarks>
/// There is no transient case here by construction. Translation touches nothing but the bytes
/// already in hand, so a translation that fails once fails identically on every redelivery —
/// which is why every failure this type can express is a dead-letter.
/// </remarks>
public sealed record MessageTranslation
{
    private MessageTranslation(IngestionJob? job, MessageDisposition? failure)
    {
        Job = job;
        Failure = failure;
    }

    /// <summary>The translated job, or <see langword="null"/> when translation failed.</summary>
    public IngestionJob? Job { get; }

    /// <summary>The dead-letter disposition, or <see langword="null"/> when translation succeeded.</summary>
    public MessageDisposition? Failure { get; }

    /// <summary>Whether the payload produced a job.</summary>
    [MemberNotNullWhen(true, nameof(Job))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Job is not null;

    /// <summary>Wraps a successfully translated job.</summary>
    /// <param name="job">The job the payload described.</param>
    public static MessageTranslation Success(IngestionJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new MessageTranslation(job, null);
    }

    /// <summary>Records a payload defect that redelivery cannot fix.</summary>
    /// <param name="reason">One of <see cref="DeadLetterReasons"/>.</param>
    /// <param name="description">The variable detail behind <paramref name="reason"/>.</param>
    public static MessageTranslation PermanentFailure(string reason, string description) =>
        new(null, MessageDisposition.DeadLetter(reason, description));
}
