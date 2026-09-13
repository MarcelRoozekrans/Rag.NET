using System.Globalization;
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
                    await CompleteCarryingLockStateAsync(deadLetters, message, strays.Count, ct);
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
    /// <summary>
    /// Settles <paramref name="message"/>, and on failure rethrows carrying the lock state the
    /// broker saw — the evidence #246 has never had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/246">#246</see>
    /// is an intermittent <c>MessageLockLost</c> on this call. It has been diagnosed twice as lock
    /// expiry and "fixed" twice by raising the queue's <c>LockDuration</c>, most recently from
    /// <c>PT1M</c> to <c>PT5M</c>. **Both fixes were inert**: instrumenting this line on 2026-09-12
    /// showed the lock carrying its <b>full 300 seconds</b> at the moment of the call, on every
    /// observed run. A lock with five minutes left has not expired, so expiry was never the
    /// mechanism.
    /// </para>
    /// <para>
    /// <b>Why a permanent diagnostic rather than a fix.</b> The mechanism is still unknown. The
    /// failure reproduced once in twenty local runs and could not be caught again — not by
    /// re-running, not by running the full project, and not under eight-way CPU pressure. Guessing
    /// a third time is how the first two fixes happened. The exception's own wording names the two
    /// remaining candidates — the message was "already removed from the queue", or "received by a
    /// different receiver instance" — and distinguishing them needs the state at the moment of
    /// failure, which is what this captures.
    /// </para>
    /// <para>
    /// <b>Silent unless it fires.</b> Nothing is written on the passing path, so test output stays
    /// pristine. Since phase 6.2.42 CI dumps a failing project's log, so the next occurrence in CI
    /// arrives with this context attached rather than costing another investigation.
    /// </para>
    /// <para>
    /// <b>Known limit.</b> The caller's <c>finally</c> abandons strays, and an exception there
    /// would replace this one. Left alone deliberately: <c>strays</c> was empty on every observed
    /// run, so the masking path has never been seen, and widening this change to cover a
    /// hypothetical would be the same over-reach as the two previous fixes.
    /// </para>
    /// </remarks>
    /// <param name="receiver">The dead-letter receiver holding the lock.</param>
    /// <param name="message">The message to settle.</param>
    /// <param name="strayCount">How many other messages this receiver is holding locks on.</param>
    /// <param name="ct">The cancellation token.</param>
    private static async Task CompleteCarryingLockStateAsync(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message,
        int strayCount,
        CancellationToken ct)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        try
        {
            await receiver.CompleteMessageAsync(message, ct);
        }
        catch (ServiceBusException ex)
        {
            throw new InvalidOperationException(
                $"Settling the dead-lettered message failed with {ex.Reason}. Lock state at the " +
                $"attempt, recorded for #246: lockedUntil={message.LockedUntil:O} " +
                $"attemptedAt={attemptedAt:O} " +
                $"lockRemaining={(message.LockedUntil - attemptedAt).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s " +
                $"deliveryCount={message.DeliveryCount} lockToken={message.LockToken} " +
                $"sequenceNumber={message.SequenceNumber} enqueuedTime={message.EnqueuedTime:O} " +
                $"straysHeld={strayCount}. " +
                "A positive lockRemaining rules out expiry and therefore rules out LockDuration " +
                "as the mechanism, which is what #246 was twice fixed for.",
                ex);
        }
    }

}
