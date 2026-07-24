namespace Rag.NET.Models.Options;

/// <summary>Options for the event-driven ingestion job queue and background processor.</summary>
public sealed class EventDrivenIngestionOptions
{
    /// <summary>
    /// Maximum number of jobs the in-memory queue holds before
    /// <c>IIngestionJobQueue.EnqueueAsync</c> waits for space (backpressure).
    /// Must be greater than zero. Default 1000.
    /// </summary>
    public int QueueCapacity { get; set; } = 1000;
}
