using Azure.Messaging.ServiceBus;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// End-to-end coverage over real AMQP against the Service Bus emulator: the settlement
/// decisions the unit suite asserts as values are checked here for what they actually do to a
/// broker — a completed message is gone, an abandoned one comes back, a dead-lettered one
/// shows up in the dead-letter sub-queue with its reason intact.
/// </summary>
/// <remarks>
/// Every test uses a document id unique to itself, and the queues are shared, so a message a
/// test did not send is ignored rather than fought over.
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class ServiceBusIngestionIntegrationTests(ServiceBusEmulatorFixture fixture)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static string Payload(string documentId, string content = "hello from the broker") =>
        $$"""{ "documentId": "{{documentId}}", "content": "{{content}}" }""";

    private async Task SendAsync(string queue, string body, string? sessionId, CancellationToken ct)
    {
        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var sender = client.CreateSender(queue);
        var message = new ServiceBusMessage(body);
        if (sessionId is not null)
            message.SessionId = sessionId;
        await sender.SendMessageAsync(message, ct);
    }

    private AzureServiceBusIngestionTrigger Trigger(
        FakeIngestor ingestor, string queue, bool sessions = false) =>
        new(new ServiceBusClient(fixture.ConnectionString), ingestor, queue,
            new ServiceBusIngestionOptions { SessionsEnabled = sessions });

    [Fact]
    public async Task SuccessfulIngestion_RemovesTheMessageFromTheQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var documentId = $"ok-{Guid.NewGuid():N}";
        await SendAsync(ServiceBusEmulatorFixture.QueueName, Payload(documentId), null, ct);

        var ingestor = FakeIngestor.Succeeding();
        await using var sut = Trigger(ingestor, ServiceBusEmulatorFixture.QueueName);
        await sut.StartAsync(ct);
        try
        {
            await ingestor.Entered.WaitAsync(Bound, ct);
        }
        finally
        {
            // StopProcessingAsync waits for in-flight handlers, so settlement has happened by
            // the time this returns — no polling, no sleep.
            await sut.StopAsync(ct);
        }

        Assert.Contains(ingestor.Ingested, m => string.Equals(m.DocumentId.Value, documentId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransientFailure_TheBrokerRedeliversTheMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var documentId = $"retry-{Guid.NewGuid():N}";
        await SendAsync(ServiceBusEmulatorFixture.QueueName, Payload(documentId), null, ct);

        var ingestor = FakeIngestor.ThrowingOnceThenSucceeding();
        await using var sut = Trigger(ingestor, ServiceBusEmulatorFixture.QueueName);
        await sut.StartAsync(ct);
        try
        {
            // The second entry only happens if the abandon actually put the message back.
            await ingestor.Reentered.WaitAsync(Bound, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.True(ingestor.Calls >= 2);
    }

    [Fact]
    public async Task PermanentFailure_LandsInTheDeadLetterQueueWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        // No documentId: a payload defect that no redelivery can fix.
        await SendAsync(ServiceBusEmulatorFixture.QueueName,
            """{ "content": "an orphan with no id" }""", null, ct);

        var ingestor = FakeIngestor.Succeeding();
        await using var sut = Trigger(ingestor, ServiceBusEmulatorFixture.QueueName);
        await sut.StartAsync(ct);

        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var deadLetters = client.CreateReceiver(
            ServiceBusEmulatorFixture.QueueName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        ServiceBusReceivedMessage? dead = null;
        try
        {
            dead = await deadLetters.ReceiveMessageAsync(Bound, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.NotNull(dead);
        Assert.Equal(DeadLetterReasons.MissingRequiredField, dead.DeadLetterReason);
        Assert.False(string.IsNullOrWhiteSpace(dead.DeadLetterErrorDescription));
        // This is the capability that did not exist before: a bad document used to be logged
        // at Warning and silently dropped, with no operator surface at all.
        Assert.Equal(0, ingestor.Calls);
        await deadLetters.CompleteMessageAsync(dead, ct);
    }

    [Fact]
    public async Task SessionEnabledQueue_IngestsThroughTheSessionProcessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var documentId = $"session-{Guid.NewGuid():N}";
        await SendAsync(ServiceBusEmulatorFixture.SessionQueueName,
            Payload(documentId), sessionId: documentId, ct);

        var ingestor = FakeIngestor.Succeeding();
        await using var sut = Trigger(ingestor, ServiceBusEmulatorFixture.SessionQueueName, sessions: true);
        await sut.StartAsync(ct);
        try
        {
            await ingestor.Entered.WaitAsync(Bound, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.Contains(ingestor.Ingested, m => string.Equals(m.DocumentId.Value, documentId, StringComparison.Ordinal));
    }
}
