using Azure.Messaging.ServiceBus;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>
/// Hands out the fake processors and records its own disposal.
/// <see cref="ServiceBusClient"/> has a protected parameterless constructor and virtual
/// factory methods, so a trigger can be driven with no namespace behind it.
/// </summary>
internal sealed class FakeServiceBusClient(
    FakeProcessor? processor = null,
    FakeSessionProcessor? sessionProcessor = null) : ServiceBusClient
{
    private int _disposes;

    public FakeProcessor Processor { get; } = processor ?? new FakeProcessor();

    public FakeSessionProcessor SessionProcessor { get; } = sessionProcessor ?? new FakeSessionProcessor();

    public string? RequestedEntity { get; private set; }

    public string? RequestedSubscription { get; private set; }

    /// <summary>Seeded <see langword="true"/> so a test proves the trigger turned it off.</summary>
    public bool AutoCompleteRequested { get; private set; } = true;

    public int MaxConcurrentCallsPerSessionRequested { get; private set; } = -1;

    /// <summary>
    /// The four knobs <see cref="ServiceBusIngestionOptions"/> exposes and validates. They are
    /// recorded here because validating a value at registration proves nothing about it
    /// reaching the processor — only this does.
    /// </summary>
    public int MaxConcurrentCallsRequested { get; private set; } = -1;

    /// <inheritdoc cref="MaxConcurrentCallsRequested"/>
    public int MaxConcurrentSessionsRequested { get; private set; } = -1;

    /// <inheritdoc cref="MaxConcurrentCallsRequested"/>
    public int PrefetchCountRequested { get; private set; } = -1;

    /// <inheritdoc cref="MaxConcurrentCallsRequested"/>
    public TimeSpan? MaxAutoLockRenewalDurationRequested { get; private set; }

    public int Disposes => Volatile.Read(ref _disposes);

    public override ServiceBusProcessor CreateProcessor(string queueName, ServiceBusProcessorOptions options)
    {
        RequestedEntity = queueName;
        RecordProcessorOptions(options);
        return Processor;
    }

    public override ServiceBusProcessor CreateProcessor(
        string topicName, string subscriptionName, ServiceBusProcessorOptions options)
    {
        RequestedEntity = topicName;
        RequestedSubscription = subscriptionName;
        RecordProcessorOptions(options);
        return Processor;
    }

    public override ServiceBusSessionProcessor CreateSessionProcessor(
        string queueName, ServiceBusSessionProcessorOptions options = default!)
    {
        RequestedEntity = queueName;
        RecordSessionProcessorOptions(options);
        return SessionProcessor;
    }

    public override ServiceBusSessionProcessor CreateSessionProcessor(
        string topicName, string subscriptionName, ServiceBusSessionProcessorOptions options = default!)
    {
        RequestedEntity = topicName;
        RequestedSubscription = subscriptionName;
        RecordSessionProcessorOptions(options);
        return SessionProcessor;
    }

    public override ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposes);
        return ValueTask.CompletedTask;
    }

    private void RecordProcessorOptions(ServiceBusProcessorOptions options)
    {
        AutoCompleteRequested = options.AutoCompleteMessages;
        MaxConcurrentCallsRequested = options.MaxConcurrentCalls;
        PrefetchCountRequested = options.PrefetchCount;
        MaxAutoLockRenewalDurationRequested = options.MaxAutoLockRenewalDuration;
    }

    private void RecordSessionProcessorOptions(ServiceBusSessionProcessorOptions options)
    {
        AutoCompleteRequested = options.AutoCompleteMessages;
        MaxConcurrentCallsPerSessionRequested = options.MaxConcurrentCallsPerSession;
        MaxConcurrentSessionsRequested = options.MaxConcurrentSessions;
        PrefetchCountRequested = options.PrefetchCount;
        MaxAutoLockRenewalDurationRequested = options.MaxAutoLockRenewalDuration;
    }
}
