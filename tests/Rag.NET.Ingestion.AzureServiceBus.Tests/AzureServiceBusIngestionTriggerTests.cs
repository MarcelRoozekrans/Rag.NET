using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Ingestion.AzureServiceBus.Settlement;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Lifecycle and settlement, without a broker. Determinism comes from
/// <see cref="TaskCompletionSource"/> signalling and bounded <c>WaitAsync</c> — the shape
/// <c>BackgroundPollingTriggerTests</c> already uses for hosted services. No sleeps.
/// </summary>
public sealed class AzureServiceBusIngestionTriggerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private const string ValidPayload = """{ "documentId": "doc-1", "content": "hello world" }""";

    private static ServiceBusReceivedMessage Message(string json = ValidPayload, int deliveryCount = 1) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(json),
            messageId: "msg-1",
            deliveryCount: deliveryCount);

    private static ServiceBusMessageIngestionHandler Handler(FakeIngestor ingestor) =>
        new(ingestor, "queue-a", NullLogger.Instance);

    [Fact]
    public async Task StartAsync_StartsProcessing_StopAsync_StopsAndDisposes()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeServiceBusClient();
        var sut = new AzureServiceBusIngestionTrigger(
            client, FakeIngestor.Succeeding(), "queue-a", new ServiceBusIngestionOptions());

        await sut.StartAsync(ct);
        Assert.Equal(1, client.Processor.Starts);
        Assert.Equal("queue-a", client.RequestedEntity);
        // Auto-complete would settle every message as a success before the outcome existed.
        Assert.False(client.AutoCompleteRequested);

        await sut.StopAsync(ct);
        Assert.Equal(1, client.Processor.Stops);

        await sut.DisposeAsync();
        Assert.Equal(1, client.Processor.Disposes);
        Assert.Equal(1, client.Disposes);

        // Idempotent: a host that disposes twice must not double-release the client.
        await sut.DisposeAsync();
        Assert.Equal(1, client.Disposes);
    }

    [Fact]
    public async Task SessionsEnabled_UsesTheSessionProcessorAndPinsPerSessionConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeServiceBusClient();
        var sut = new AzureServiceBusIngestionTrigger(
            client, FakeIngestor.Succeeding(), "queue-a",
            new ServiceBusIngestionOptions
            {
                SessionsEnabled = true,
                MaxConcurrentSessions = 4,
                PrefetchCount = 7,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(3),
            });

        await sut.StartAsync(ct);
        try
        {
            Assert.Equal(1, client.SessionProcessor.Starts);
            Assert.Equal(0, client.Processor.Starts);
            // Per-document FIFO is the only thing sessions buy; more than one concurrent call
            // per session would throw it away.
            Assert.Equal(1, client.MaxConcurrentCallsPerSessionRequested);
            // The knobs the options expose have to arrive at the processor. Setting one and
            // never asserting it landed is how a dropped assignment ships unnoticed.
            Assert.Equal(4, client.MaxConcurrentSessionsRequested);
            Assert.Equal(7, client.PrefetchCountRequested);
            Assert.Equal(TimeSpan.FromMinutes(3), client.MaxAutoLockRenewalDurationRequested);
        }
        finally
        {
            await sut.StopAsync(ct);
            await sut.DisposeAsync();
        }

        Assert.Equal(1, client.SessionProcessor.Stops);
        Assert.Equal(1, client.SessionProcessor.Disposes);
    }

    [Fact]
    public async Task NonSessionOptions_ReachTheProcessor()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeServiceBusClient();
        await using var sut = new AzureServiceBusIngestionTrigger(
            client, FakeIngestor.Succeeding(), "queue-a",
            new ServiceBusIngestionOptions
            {
                MaxConcurrentCalls = 5,
                PrefetchCount = 12,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(9),
            });

        await sut.StartAsync(ct);
        await sut.StopAsync(ct);

        Assert.Equal(5, client.MaxConcurrentCallsRequested);
        Assert.Equal(12, client.PrefetchCountRequested);
        Assert.Equal(TimeSpan.FromMinutes(9), client.MaxAutoLockRenewalDurationRequested);
        Assert.False(client.AutoCompleteRequested);
    }

    [Fact]
    public async Task SubscriptionName_ConsumesTheTopicSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeServiceBusClient();
        await using var sut = new AzureServiceBusIngestionTrigger(
            client, FakeIngestor.Succeeding(), "topic-a",
            new ServiceBusIngestionOptions { SubscriptionName = "sub-a" });

        await sut.StartAsync(ct);
        await sut.StopAsync(ct);

        Assert.Equal("topic-a", client.RequestedEntity);
        Assert.Equal("sub-a", client.RequestedSubscription);
    }

    [Fact]
    public async Task SuccessfulIngestion_CompletesTheMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestor = FakeIngestor.Succeeding();
        var args = new RecordingMessageEventArgs(Message(), ct);

        await Handler(ingestor).HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.Complete, args.Applied);
        Assert.Equal(1, ingestor.Calls);
        Assert.Equal("doc-1", ingestor.Ingested[0].DocumentId.Value);
    }

    [Fact]
    public async Task TransientFailure_AbandonsForRedelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var args = new RecordingMessageEventArgs(Message(), ct);

        await Handler(FakeIngestor.Throwing(new IOException("the disk went away")))
            .HandleAsync(args).WaitAsync(Bound, ct);

        // Abandon, not dead-letter: the broker redelivers and counts the attempt, and
        // MaxDeliveryCount is what eventually dead-letters a message that keeps failing.
        Assert.Equal(SettleAction.Abandon, args.Applied);
    }

    [Fact]
    public async Task TransientIngestionError_AbandonsForRedelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var args = new RecordingMessageEventArgs(Message(), ct);

        await Handler(FakeIngestor.Failing(new RagError.StorageFailed(new IOException("vector store down"))))
            .HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.Abandon, args.Applied);
    }

    [Fact]
    public async Task PermanentFailure_DeadLettersWithAReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestor = FakeIngestor.Succeeding();
        var args = new RecordingMessageEventArgs(Message("""{ "content": "no id here" }"""), ct);

        await Handler(ingestor).HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.DeadLetter, args.Applied);
        Assert.Equal(DeadLetterReasons.MissingRequiredField, args.DeadLetterReason);
        Assert.False(string.IsNullOrWhiteSpace(args.DeadLetterDescription));
        // The payload never reached the pipeline.
        Assert.Equal(0, ingestor.Calls);
    }

    [Fact]
    public async Task PermanentIngestionError_DeadLettersWithAReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var args = new RecordingMessageEventArgs(Message(), ct);

        await Handler(FakeIngestor.Failing(new RagError.NoParserFound("application/x-nonsense")))
            .HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.DeadLetter, args.Applied);
        Assert.Equal(DeadLetterReasons.IngestionRejected, args.DeadLetterReason);
        Assert.Contains("application/x-nonsense", args.DeadLetterDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostShutdown_DoesNotSettleInFlightMessagesAsFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ingestor = FakeIngestor.Blocking();
        var args = new RecordingMessageEventArgs(Message(), shutdown.Token);

        var handling = Handler(ingestor).HandleAsync(args);

        // Only cancel once ingestion is provably in flight, so the assertion is about
        // shutdown-during-ingestion rather than a race with the handler starting.
        await ingestor.Entered.WaitAsync(Bound, ct);
        await shutdown.CancelAsync();
        await handling.WaitAsync(Bound, ct);

        // Not abandoned, not dead-lettered: left unsettled so the lock expires and the broker
        // redelivers without charging the message a delivery attempt it never really had.
        Assert.Null(args.Applied);
    }

    [Fact]
    public async Task HostShutdown_DoesNotSettleWhenTheIngestorReportsCancellationAsAFailedResult()
    {
        var ct = TestContext.Current.CancellationToken;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A permanent error, so anything that treats this as a real failure dead-letters it —
        // the worst outcome, because the document is healthy and the host merely restarted.
        var ingestor = FakeIngestor.ReportingCancellationAsFailure(
            new RagError.NoParserFound("application/x-nonsense"));
        var args = new RecordingMessageEventArgs(Message(), shutdown.Token);

        var handling = Handler(ingestor).HandleAsync(args);

        await ingestor.Entered.WaitAsync(Bound, ct);
        await shutdown.CancelAsync();
        await handling.WaitAsync(Bound, ct);

        // Observing cancellation and returning a failed Result is a legitimate IIngestor shape;
        // only PipelineIngestor happens to rethrow. Shutdown must outrank the error either way,
        // or every host restart burns a delivery attempt on whatever was in flight.
        Assert.Null(args.Applied);
    }

    [Fact]
    public async Task DeadLetterDescription_IsTruncatedByUtf8BytesNotCharacters()
    {
        var ct = TestContext.Current.CancellationToken;
        // Four-byte runes: 3000 characters, but 6000 bytes — past the 4096-byte cap Service Bus
        // enforces on DeadLetterErrorDescription, while a character count would call it fine.
        var oversized = string.Concat(Enumerable.Repeat("\U0001F600", 1500));
        var args = new RecordingMessageEventArgs(Message(), ct);

        await Handler(FakeIngestor.Failing(new RagError.NoParserFound(oversized)))
            .HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.DeadLetter, args.Applied);
        var description = args.DeadLetterDescription!;
        var bytes = Encoding.UTF8.GetByteCount(description);
        Assert.True(bytes <= 4000, $"Description encodes to {bytes} UTF-8 bytes.");
        // Truncated, not gutted: a per-character cut would have kept 4000 chars / 16000 bytes.
        Assert.True(bytes > 3900, $"Description encodes to only {bytes} UTF-8 bytes.");
        // Cut on a rune boundary. A split surrogate pair — or any ill-formed tail — decodes as
        // U+FFFD, and the input carries none of its own.
        Assert.All(description.EnumerateRunes(), rune => Assert.NotEqual(Rune.ReplacementChar, rune));
    }

    [Fact]
    public async Task MalformedPayload_DeadLettersWithoutTouchingTheIngestor()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestor = FakeIngestor.Succeeding();
        var args = new RecordingMessageEventArgs(Message("not json"), ct);

        await Handler(ingestor).HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.DeadLetter, args.Applied);
        Assert.Equal(DeadLetterReasons.MalformedPayload, args.DeadLetterReason);
        Assert.Equal(0, ingestor.Calls);
    }

    [Fact]
    public async Task SessionMessage_SettlesThroughTheSessionOverload()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestor = FakeIngestor.Succeeding();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(ValidPayload), messageId: "msg-1", sessionId: "doc-1");
        var args = new RecordingSessionMessageEventArgs(message, ct);

        await Handler(ingestor).HandleAsync(args).WaitAsync(Bound, ct);

        Assert.Equal(SettleAction.Complete, args.Applied);
        Assert.Equal("doc-1", ingestor.Ingested[0].DocumentId.Value);
    }

    [Fact]
    public async Task SessionIdDisagreeingWithDocumentId_DeadLettersThroughTheSessionOverload()
    {
        var ct = TestContext.Current.CancellationToken;
        var ingestor = FakeIngestor.Succeeding();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(ValidPayload), messageId: "msg-1", sessionId: "tenant-acme");
        var args = new RecordingSessionMessageEventArgs(message, ct);

        await Handler(ingestor).HandleAsync(args).WaitAsync(Bound, ct);

        // A session that does not name the document it orders is a producer bug: the FIFO
        // guarantee sessions exist for would be silently meaningless.
        Assert.Equal(SettleAction.DeadLetter, args.Applied);
        Assert.Equal(DeadLetterReasons.SessionDocumentMismatch, args.DeadLetterReason);
        Assert.Equal(0, ingestor.Calls);
    }

    [Fact]
    public void Constructor_RejectsMissingArguments()
    {
        var options = new ServiceBusIngestionOptions();
        Assert.Throws<ArgumentNullException>(() => new AzureServiceBusIngestionTrigger(
            null!, FakeIngestor.Succeeding(), "queue-a", options));
        Assert.Throws<ArgumentNullException>(() => new AzureServiceBusIngestionTrigger(
            new FakeServiceBusClient(), null!, "queue-a", options));
        Assert.Throws<ArgumentException>(() => new AzureServiceBusIngestionTrigger(
            new FakeServiceBusClient(), FakeIngestor.Succeeding(), "  ", options));
        Assert.Throws<ArgumentNullException>(() => new AzureServiceBusIngestionTrigger(
            new FakeServiceBusClient(), FakeIngestor.Succeeding(), "queue-a", null!));
    }
}
