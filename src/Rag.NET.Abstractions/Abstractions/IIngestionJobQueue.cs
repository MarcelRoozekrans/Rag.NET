using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Producer/consumer queue decoupling ingestion triggers (webhooks, pollers, message buses)
/// from the background processor that performs the actual ingestion.
/// </summary>
public interface IIngestionJobQueue
{
    /// <summary>
    /// Enqueues a job. When the queue is full, waits until space is available
    /// (backpressure — jobs are never dropped).
    /// </summary>
    ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes jobs in FIFO order as they become available, until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<IngestionJob> DequeueAllAsync(CancellationToken cancellationToken = default);
}
