using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
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

    private async Task SendAsync(
        string queue, string body, string? sessionId, CancellationToken ct, string? messageId = null)
    {
        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var sender = client.CreateSender(queue);
        var message = new ServiceBusMessage(body);
        if (sessionId is not null)
            message.SessionId = sessionId;
        if (messageId is not null)
            message.MessageId = messageId;
        await sender.SendMessageAsync(message, ct);
    }

    private AzureServiceBusIngestionTrigger Trigger(
        FakeIngestor ingestor, string queue, bool sessions = false,
        ILogger<AzureServiceBusIngestionTrigger>? logger = null) =>
        new(new ServiceBusClient(fixture.ConnectionString), ingestor, queue,
            new ServiceBusIngestionOptions { SessionsEnabled = sessions }, logger);

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
        // sub-queue and assert that this document never reached the pipeline. It doubles as the
        // MessageId, which is what the trigger's dead-letter log names.
        var documentId = $"reject-{Guid.NewGuid():N}";
        await SendAsync(ServiceBusEmulatorFixture.QueueName,
            $$"""{ "documentId": "{{documentId}}" }""", null, ct, messageId: documentId);

        var ingestor = FakeIngestor.Succeeding();
        var deadLettered = new DeadLetterSignal(documentId);
        await using var sut = Trigger(ingestor, ServiceBusEmulatorFixture.QueueName, logger: deadLettered);
        await sut.StartAsync(ct);
        try
        {
            // The handler logs the decision before it settles. StopAsync below waits for the
            // in-flight handler, so once it returns the dead-letter has been acknowledged by the
            // broker: no polling, no sleep.
            await deadLettered.Task.WaitAsync(Bound, ct);
        }
        finally
        {
            await sut.StopAsync(ct);
        }

        var dead = await PeekDeadLetterAsync(documentId, ct);

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
    /// Finds this test's own message in the shared dead-letter sub-queue by peeking it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Peek, never receive.</b> Receiving from the dead-letter sub-queue trips a defect in the
    /// Service Bus emulator, and that defect is #246. The first receive-mode read of a queue's
    /// dead-letter sub-queue, shortly after the first dead-letter on a freshly started emulator,
    /// makes the emulator add the same sequence number to its dead-letter cursor twice. Its log
    /// says <c>Cannot add the same item 'S.SP-SequenceNumber:1' more than once</c>, and it then
    /// faults and aborts the whole queue entity. Every link on the queue is force-detached,
    /// including the receiver that has just been handed the message. The settle that follows
    /// sees its link close mid-disposition, which the SDK reports as <c>MessageLockLost</c> with
    /// the full lock remaining. The broker reloads the entity and the message comes back at
    /// <c>deliveryCount</c> 1.
    /// </para>
    /// <para>
    /// Measured on 2026-11-13 against emulator 2.0.1 (the newest tag), cold start, four emulators
    /// in parallel: a receive failed 21 times in 64 runs and every failure carried that
    /// emulator log line. A receive five seconds later failed 0 times in 16, and a peek right
    /// after the dead-letter failed 0 times in 16. The emulator is closed source; the defect is
    /// reported upstream as Azure/azure-service-bus-emulator-installer#157.
    /// </para>
    /// <para>
    /// A peek reads the same <c>DeadLetterReason</c> and <c>DeadLetterErrorDescription</c>
    /// without a receive link or a lock. Rag.NET never reads its own dead-letter sub-queue, so
    /// receiving from it was never the behaviour under test. This avoids the emulator defect; it
    /// does not fix it. A product test that has to receive from a dead-letter sub-queue on this
    /// emulator would hit the same fault.
    /// </para>
    /// </remarks>
    private async Task<ServiceBusReceivedMessage?> PeekDeadLetterAsync(
        string documentId, CancellationToken ct)
    {
        await using var client = new ServiceBusClient(fixture.ConnectionString);
        await using var deadLetters = client.CreateReceiver(
            ServiceBusEmulatorFixture.QueueName,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var peeked = await deadLetters.PeekMessagesAsync(PeekDepth, fromSequenceNumber: 0, ct);

        return peeked.FirstOrDefault(
            m => m.Body.ToString().Contains(documentId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Completes when the trigger logs that it dead-lettered the message with the given id.
    /// </summary>
    /// <remarks>
    /// Keyed on the message id rather than on any dead-letter log, for the same reason
    /// <see cref="FakeIngestor.WaitFor"/> is keyed: the queue is shared, so an unkeyed signal
    /// could wake on a sibling test's message.
    /// </remarks>
    private sealed class DeadLetterSignal(string messageId) : ILogger<AzureServiceBusIngestionTrigger>
    {
        private readonly TaskCompletionSource _deadLettered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _deadLettered.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!string.Equals(eventId.Name, "message_dead_lettered", StringComparison.Ordinal))
                return;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields &&
                fields.Any(f => string.Equals(f.Key, "MessageId", StringComparison.Ordinal) &&
                    string.Equals(f.Value as string, messageId, StringComparison.Ordinal)))
            {
                _deadLettered.TrySetResult();
            }
        }
    }
}
