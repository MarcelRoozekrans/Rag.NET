using Azure.Messaging.ServiceBus;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Records which settlement the handler applied instead of talking to a broker.
/// <see cref="ProcessMessageEventArgs"/> is unsealed, has a public constructor, and its settle
/// methods are virtual — so the whole settlement path is observable offline.
/// </summary>
internal sealed class RecordingMessageEventArgs(
    ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    : ProcessMessageEventArgs(message, new FakeReceiver(), cancellationToken)
{
    /// <summary><see langword="null"/> means the message was left unsettled.</summary>
    public SettleAction? Applied { get; private set; }

    public string? DeadLetterReason { get; private set; }

    public string? DeadLetterDescription { get; private set; }

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
        DeadLetterDescription = deadLetterErrorDescription;
        return Task.CompletedTask;
    }
}
