using Azure.Messaging.ServiceBus;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>The session-enabled counterpart of <see cref="FakeProcessor"/>.</summary>
internal sealed class FakeSessionProcessor : ServiceBusSessionProcessor
{
    private readonly FakeProcessor _inner = new();
    private int _starts;
    private int _stops;
    private int _disposes;

    public int Starts => Volatile.Read(ref _starts);

    public int Stops => Volatile.Read(ref _stops);

    public int Disposes => Volatile.Read(ref _disposes);

    /// <summary>
    /// The session processor's event accessors delegate to this, which the protected
    /// constructor leaves null — subscribing a handler would NullReference without it.
    /// </summary>
    protected override ServiceBusProcessor InnerProcessor => _inner;

    public override Task StartProcessingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _starts);
        return Task.CompletedTask;
    }

    public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stops);
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="FakeProcessor.CloseAsync"/>
    public override Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disposes);
        return Task.CompletedTask;
    }
}
