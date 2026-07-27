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
/// Every test uses a document id unique to itself and the queues are shared, so both halves of
/// a test are keyed on that id: the assertions, and — through
/// <see cref="FakeIngestor.WaitFor"/> — the wake-up. An unkeyed signal would let a test wake on
/// a message another test sent, stop its trigger, and assert against a document that never
/// arrived.
/// </remarks>
[Collection(ServiceBusEmulatorCollection.Name)]
public sealed class ServiceBusIngestionIntegrationTests(ServiceBusEmulatorFixture fixture)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>How many messages a broker-side check will look through on the shared queue.</summary>
    private const int PeekDepth = 200;

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

    /// <summary>
    /// Whether the queue still holds a message carrying <paramref name="marker"/> in its body.
    /// </summary>
    /// <remarks>
    /// Peek rather than receive: it is non-destructive, so it neither consumes nor charges a
    /// delivery attempt to a message belonging to another test on this shared queue. No polling
    /// either — <c>StopProcessingAsync</c> waits for in-flight handlers, and settlement is a
    /// synchronous AMQP disposition, so by the time the caller reaches here the broker has
    /// already applied whatever the handler decided.
    /// </remarks>
    private async Task<bool> QueueStillHoldsAsync(string queue, string marker, CancellationToken ct)
    {
        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var receiver = client.CreateReceiver(queue);
        var peeked = await receiver.PeekMessagesAsync(PeekDepth, fromSequenceNumber: 0, ct);

        return peeked.Any(m => m.Body.ToString().Contains(marker, StringComparison.Ordinal));
    }

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
            await ingestor.WaitFor(documentId).WaitAsync(Bound, ct);
        }
        finally
        {
            // StopProcessingAsync waits for in-flight handlers, so settlement has happened by
            // the time this returns — no polling, no sleep.
            await sut.StopAsync(ct);
        }

        Assert.Contains(ingestor.Ingested, m => string.Equals(m.DocumentId.Value, documentId, StringComparison.Ordinal));
        // The assertion that names this test: the *broker* no longer has the message. Seeing
        // the fake ingestor run proves only that the message was delivered — it would hold just
        // as well if the handler abandoned instead of completing, which is the bug that matters
        // here because an uncompleted success redelivers and re-ingests forever.
        Assert.False(
            await QueueStillHoldsAsync(ServiceBusEmulatorFixture.QueueName, documentId, ct),
            $"Document {documentId} ingested successfully but is still on the queue — it was not completed.");
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
            // The second entry for this document only happens if the abandon actually put the
            // message back.
            await ingestor.WaitFor(documentId, entries: 2).WaitAsync(Bound, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.True(ingestor.CallsFor(documentId) >= 2);
    }

    [Fact]
    public async Task PermanentFailure_LandsInTheDeadLetterQueueWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        // A documentId with no content: a payload defect that no redelivery can fix. The id is
        // present purely so this test can recognise its own message in a shared dead-letter
        // sub-queue and assert that this document never reached the pipeline.
        var documentId = $"reject-{Guid.NewGuid():N}";
        await SendAsync(ServiceBusEmulatorFixture.QueueName,
            $$"""{ "documentId": "{{documentId}}" }""", null, ct);

        var ingestor = FakeIngestor.Succeeding();
        await using var sut = Trigger(ingestor, ServiceBusEmulatorFixture.QueueName);
        await sut.StartAsync(ct);

        ServiceBusReceivedMessage? dead;
        try
        {
            dead = await ReceiveDeadLetterAsync(documentId, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        Assert.NotNull(dead);
        Assert.Equal(DeadLetterReasons.MissingRequiredField, dead.DeadLetterReason);
        Assert.False(string.IsNullOrWhiteSpace(dead.DeadLetterErrorDescription));
        // This is the capability that did not exist before: a bad document used to be logged
        // at Warning and silently dropped, with no operator surface at all. Keyed on this
        // test's own id, so a stray message from a sibling cannot satisfy or break it.
        Assert.Equal(0, ingestor.CallsFor(documentId));
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
            await ingestor.WaitFor(documentId).WaitAsync(Bound, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        // No broker-side absence check here: a plain receiver cannot peek a session-required
        // entity, and accepting a session just to look would race the trigger's own session
        // lock. Completion on the success path is proven on the non-session queue above, and
        // the two overloads share one DecideAsync — this test's job is that the session
        // processor is the one doing the work.
        Assert.Contains(ingestor.Ingested, m => string.Equals(m.DocumentId.Value, documentId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Pulls this test's own message out of the shared dead-letter sub-queue, holding the locks
    /// on anything else it finds so the same strays are not handed back on the next receive,
    /// then releasing them untouched.
    /// </summary>
    private async Task<ServiceBusReceivedMessage?> ReceiveDeadLetterAsync(
        string documentId, CancellationToken ct)
    {
        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var deadLetters = client.CreateReceiver(
            ServiceBusEmulatorFixture.QueueName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        var strays = new List<ServiceBusReceivedMessage>();
        try
        {
            while (true)
            {
                var message = await deadLetters.ReceiveMessageAsync(Bound, ct);
                if (message is null)
                    return null;

                if (message.Body.ToString().Contains(documentId, StringComparison.Ordinal))
                {
                    await deadLetters.CompleteMessageAsync(message, ct);
                    return message;
                }

                strays.Add(message);
            }
        }
        finally
        {
            foreach (var stray in strays)
                await deadLetters.AbandonMessageAsync(stray, propertiesToModify: null, ct);
        }
    }
}
