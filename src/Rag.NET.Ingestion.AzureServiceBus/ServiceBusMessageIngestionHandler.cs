using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Ingestion.AzureServiceBus.Logging;
using Rag.NET.Ingestion.AzureServiceBus.Messaging;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Rag.NET.Models;

namespace Rag.NET.Ingestion.AzureServiceBus;

/// <summary>
/// Ingests one message, decides how it should be settled, and applies that decision to
/// whichever event-args type the SDK handed the callback.
/// </summary>
/// <remarks>
/// <para>
/// Deciding is separated from settling so the interesting half is a function from a
/// <see cref="ServiceBusReceivedMessage"/> to a <see cref="MessageDisposition"/> — assertable
/// with nothing but <c>ServiceBusModelFactory</c> and a fake <see cref="IIngestor"/>.
/// </para>
/// <para>
/// It calls <see cref="IIngestor.IngestAsync"/> directly and never touches
/// <c>IIngestionJobQueue</c>. Handing a durable broker message to an in-memory bounded channel
/// and settling it turns at-least-once into at-most-once the moment the process crashes with
/// the channel non-empty — the loss window this transport exists to close. Settling after the
/// channel drains is not expressible through that interface either: <c>EnqueueAsync</c>
/// returns no completion signal and <c>IngestionJob</c> carries no correlation handle.
/// </para>
/// </remarks>
internal sealed class ServiceBusMessageIngestionHandler(
    IIngestor ingestor,
    string entityPath,
    ILogger logger)
{
    /// <summary>
    /// Service Bus caps <c>DeadLetterErrorDescription</c> at 4096 <b>bytes</b>, not characters;
    /// stay well inside it. Truncating by character count would let non-ASCII detail — a file
    /// name in Japanese, an error message with a smart quote — encode past the cap and fail the
    /// dead-letter call, leaving the message unsettled to loop until <c>MaxDeliveryCount</c>.
    /// </summary>
    private const int MaxDescriptionBytes = 4000;

    /// <summary>
    /// Ingests the message and returns the settlement it earned. Never throws for an ingestion
    /// failure — every path resolves to a disposition.
    /// </summary>
    public async Task<MessageDisposition> DecideAsync(
        ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        var translation = ServiceBusMessageTranslator.Translate(message);
        if (!translation.IsSuccess)
        {
            ServiceBusIngestionLog.MessageDeadLettered(logger, entityPath, message.MessageId,
                translation.Failure.Reason!, translation.Failure.Description);
            return translation.Failure;
        }

        var job = translation.Job;
        try
        {
            using var content = new MemoryStream(job.Content, writable: false);
            var result = await ingestor
                .IngestAsync(content, job.Metadata, job.Options, progress: null, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                // Shutdown outranks the error. An IIngestor that observes cancellation and
                // reports it as a failed Result rather than throwing is well behaved, and
                // settling that as a failure would burn a delivery attempt — or dead-letter a
                // healthy document — once per host restart.
                return cancellationToken.IsCancellationRequested
                    ? LeaveForShutdown(message)
                    : FromFailedResult(message, result.Error);
            }

            ServiceBusIngestionLog.MessageCompleted(
                logger, entityPath, job.Metadata.DocumentId.Value, message.MessageId);
            return MessageDisposition.Complete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return LeaveForShutdown(message);
        }
        catch (Exception ex)
        {
            // Unclassified by construction: an exception escaping the pipeline says nothing
            // about whether a retry helps, so redeliver and let the broker's MaxDeliveryCount
            // be the backstop that dead-letters a message which keeps doing this.
            ServiceBusIngestionLog.MessageAbandonedAfterThrow(
                logger, entityPath, message.MessageId, message.DeliveryCount, ex);
            return MessageDisposition.Abandon;
        }
    }

    /// <summary>Handles one message from a non-session processor.</summary>
    public async Task HandleAsync(ProcessMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var disposition = await DecideAsync(args.Message, args.CancellationToken).ConfigureAwait(false);

        try
        {
            switch (disposition.Action)
            {
                case SettleAction.Complete:
                    await args.CompleteMessageAsync(args.Message, SettleToken).ConfigureAwait(false);
                    break;
                case SettleAction.Abandon:
                    await args.AbandonMessageAsync(args.Message, propertiesToModify: null, SettleToken)
                        .ConfigureAwait(false);
                    break;
                case SettleAction.DeadLetter:
                    await args.DeadLetterMessageAsync(args.Message, disposition.Reason!,
                        disposition.Description, SettleToken).ConfigureAwait(false);
                    break;
                default:
                    break; // Leave — the lock expires and the broker redelivers.
            }
        }
        catch (Exception ex)
        {
            LogSettlementFailure(args.Message, disposition, ex);
        }
    }

    /// <summary>Handles one message from a session processor.</summary>
    /// <remarks>
    /// A near-copy of the non-session overload because <see cref="ProcessMessageEventArgs"/>
    /// and <see cref="ProcessSessionMessageEventArgs"/> share no base type and no interface in
    /// the SDK. Both delegate the whole decision to <see cref="DecideAsync"/>; only the four
    /// settle calls are restated.
    /// </remarks>
    public async Task HandleAsync(ProcessSessionMessageEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var disposition = await DecideAsync(args.Message, args.CancellationToken).ConfigureAwait(false);

        try
        {
            switch (disposition.Action)
            {
                case SettleAction.Complete:
                    await args.CompleteMessageAsync(args.Message, SettleToken).ConfigureAwait(false);
                    break;
                case SettleAction.Abandon:
                    await args.AbandonMessageAsync(args.Message, propertiesToModify: null, SettleToken)
                        .ConfigureAwait(false);
                    break;
                case SettleAction.DeadLetter:
                    await args.DeadLetterMessageAsync(args.Message, disposition.Reason!,
                        disposition.Description, SettleToken).ConfigureAwait(false);
                    break;
                default:
                    break; // Leave — the lock expires and the broker redelivers.
            }
        }
        catch (Exception ex)
        {
            LogSettlementFailure(args.Message, disposition, ex);
        }
    }

    /// <summary>
    /// Settlement runs to completion on its own token rather than the callback's.
    /// </summary>
    /// <remarks>
    /// The callback token is cancelled when the processor is torn down, and by then the
    /// outcome has already been decided — passing that token would guarantee the settle call
    /// fails and turn a completed ingestion into a redelivery. The shutdown case is handled
    /// where it belongs, by <see cref="DecideAsync"/> returning
    /// <see cref="SettleAction.Leave"/> before any settlement is attempted.
    /// </remarks>
    private static CancellationToken SettleToken => CancellationToken.None;

    /// <summary>
    /// Shutdown, not failure. Leaving the message unsettled lets its lock expire and the broker
    /// redeliver it; settling it as a failure would charge a delivery attempt to a message that
    /// never got a real one.
    /// </summary>
    private MessageDisposition LeaveForShutdown(ServiceBusReceivedMessage message)
    {
        ServiceBusIngestionLog.MessageLeftForShutdown(logger, entityPath, message.MessageId);
        return MessageDisposition.Leave;
    }

    /// <summary>
    /// The deliberate "log it and let the lock expire" path, reached from a catch that is broad
    /// on purpose.
    /// </summary>
    /// <remarks>
    /// A settle against a link the processor is tearing down surfaces as anything from
    /// <see cref="ServiceBusException"/> to <see cref="ObjectDisposedException"/> to
    /// <see cref="TaskCanceledException"/>, and the next SDK version is free to add another.
    /// Narrowing this to a list would let the unlisted one escape the callback, which buys
    /// nothing: the fallback here — leave it unsettled, the lock expires, the broker
    /// redelivers — is the safe outcome for every one of them.
    /// </remarks>
    private void LogSettlementFailure(
        ServiceBusReceivedMessage message, MessageDisposition disposition, Exception exception) =>
        ServiceBusIngestionLog.SettlementFailed(
            logger, entityPath, message.MessageId, disposition.Action.ToString(), exception);

    private MessageDisposition FromFailedResult(ServiceBusReceivedMessage message, RagError error)
    {
        var detail = Truncate(error.ToString() ?? error.GetType().Name);

        if (SettlePolicy.For(SettlePolicy.Classify(error)) == SettleAction.DeadLetter)
        {
            ServiceBusIngestionLog.MessageDeadLettered(
                logger, entityPath, message.MessageId, DeadLetterReasons.IngestionRejected, detail);
            return MessageDisposition.DeadLetter(DeadLetterReasons.IngestionRejected, detail);
        }

        ServiceBusIngestionLog.MessageAbandoned(
            logger, entityPath, message.MessageId, message.DeliveryCount, detail);
        return MessageDisposition.Abandon;
    }

    /// <summary>
    /// Trims <paramref name="value"/> to at most <see cref="MaxDescriptionBytes"/> UTF-8 bytes,
    /// cutting only on a rune boundary so neither a surrogate pair nor a multi-byte sequence is
    /// split. <c>EnumerateRunes</c> yields U+FFFD for an ill-formed subsequence, which is what
    /// the UTF-8 encoder substitutes for it too, so the running count stays honest.
    /// </summary>
    private static string Truncate(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxDescriptionBytes)
            return value;

        var bytes = 0;
        var chars = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxDescriptionBytes)
                break;

            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }

        return value[..chars];
    }
}
