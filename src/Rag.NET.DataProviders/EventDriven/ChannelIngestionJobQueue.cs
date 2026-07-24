using System.Threading.Channels;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.DataProviders.EventDriven;

/// <summary>
/// In-memory <see cref="IIngestionJobQueue"/> backed by a bounded <see cref="Channel{T}"/>.
/// A full queue applies backpressure (<see cref="BoundedChannelFullMode.Wait"/>) — enqueuers
/// wait for space; jobs are never dropped.
/// </summary>
public sealed class ChannelIngestionJobQueue : IIngestionJobQueue
{
    private readonly Channel<IngestionJob> _channel;

    /// <summary>
    /// Creates the queue with capacity <see cref="EventDrivenIngestionOptions.QueueCapacity"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="EventDrivenIngestionOptions.QueueCapacity"/> is not positive.
    /// </exception>
    public ChannelIngestionJobQueue(EventDrivenIngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.QueueCapacity, nameof(options));

        _channel = Channel.CreateBounded<IngestionJob>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <inheritdoc/>
    public ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(job, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<IngestionJob> DequeueAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
