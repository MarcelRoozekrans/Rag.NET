using Azure.Messaging.ServiceBus;

namespace Rag.NET.Ingestion.AzureServiceBus.Tests;

/// <summary>Counts start / stop / dispose without opening an AMQP link.</summary>
internal sealed class FakeProcessor : ServiceBusProcessor
{
    private int _starts;
    private int _stops;
    private int _disposes;

    public int Starts => Volatile.Read(ref _starts);

    public int Stops => Volatile.Read(ref _stops);

    public int Disposes => Volatile.Read(ref _disposes);

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

    /// <remarks>
    /// <c>DisposeAsync</c> is sealed on the processor types; it delegates to
    /// <c>CloseAsync</c>, which is virtual. Counting closes therefore counts disposals.
    /// </remarks>
    public override Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _disposes);
        return Task.CompletedTask;
    }
}
