using Azure.Messaging.ServiceBus;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// The session-processor counterpart of <see cref="RecordingMessageEventArgs"/>. It exists as
/// its own type because the SDK's two event-args types share no base and no interface, which
/// is also why the handler has two near-identical settle overloads.
/// </summary>
internal sealed class RecordingSessionMessageEventArgs(
    ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    : ProcessSessionMessageEventArgs(message, new FakeSessionReceiver(), cancellationToken)
{
    /// <summary><see langword="null"/> means the message was left unsettled.</summary>
    public SettleAction? Applied { get; private set; }

    public string? DeadLetterReason { get; private set; }

    public override Task CompleteMessageAsync(
        ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        Applied = SettleAction.Complete;
        return Task.CompletedTask;
    }

    public override Task AbandonMessageAsync(
        ServiceBusReceivedMessage message,
        IDictionary<string, object>? propertiesToModify = default,
        CancellationToken cancellationToken = default)
    {
        Applied = SettleAction.Abandon;
        return Task.CompletedTask;
    }

    public override Task DeadLetterMessageAsync(
        ServiceBusReceivedMessage message,
        string deadLetterReason,
        string? deadLetterErrorDescription = default,
        CancellationToken cancellationToken = default)
    {
        Applied = SettleAction.DeadLetter;
        DeadLetterReason = deadLetterReason;
        return Task.CompletedTask;
    }
}
