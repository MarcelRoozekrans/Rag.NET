namespace Rag.NET.Ingestion.AzureServiceBus;

/// <summary>
/// Tuning for one <see cref="AzureServiceBusIngestionTrigger"/>. Every registration gets its
/// own instance, so two queues never share a knob.
/// </summary>
/// <remarks>
/// Two settings are deliberately <b>not</b> exposed. <c>AutoCompleteMessages</c> is pinned off:
/// the whole point of this trigger is that the outcome of ingestion decides the settlement, and
/// auto-complete would settle every message as a success before the outcome existed. And on a
/// session-enabled entity the concurrency <i>within</i> a session is pinned to one, because
/// per-document FIFO is the only thing sessions buy here and a second concurrent call per
/// session destroys it.
/// </remarks>
public sealed class ServiceBusIngestionOptions
{
    /// <summary>
    /// Subscription to consume when the entity is a topic. <see langword="null"/> (the
    /// default) means the entity named at registration is a queue.
    /// </summary>
    public string? SubscriptionName { get; set; }

    /// <summary>
    /// Whether the entity is session-enabled. Opt-in, and it must match the entity: a session
    /// processor against a non-session queue fails at start, and vice versa.
    /// </summary>
    /// <remarks>
    /// With sessions on, the producer sets <c>SessionId</c> to the document id and the broker
    /// serialises that document's messages through one consumer — per-document FIFO. That is
    /// the one guarantee no other ingestion path in this library offers: nothing else holds a
    /// per-<c>DocumentId</c> lock, so two hosts ingesting the same document otherwise
    /// interleave with no coordination.
    /// </remarks>
    public bool SessionsEnabled { get; set; }

    /// <summary>
    /// Messages processed concurrently on a non-session entity. Defaults to <c>1</c>, matching
    /// the SDK, because ingestion is parse-chunk-embed-store and the useful parallelism knob
    /// is usually further down the pipeline.
    /// </summary>
    /// <remarks>
    /// Raising this above <c>1</c> on a non-session entity is enough, on its own and inside a
    /// single host, to let two ingests of the same document interleave: nothing in the pipeline
    /// holds a per-<c>DocumentId</c> lock, so a <c>Remove</c>/<c>Add</c> pair from one message
    /// can be split by another's and leave duplicate BM25 postings behind. The hazard usually
    /// gets described as "competing consumers", which understates it — this knob turns it on
    /// without a second host being involved. Enable <see cref="SessionsEnabled"/> to serialise
    /// per document, or leave this at <c>1</c>.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 1;

    /// <summary>
    /// Sessions processed concurrently on a session-enabled entity — that is, how many
    /// distinct documents move at once. Defaults to <c>8</c>. Ordering within each session is
    /// preserved regardless.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 8;

    /// <summary>
    /// Messages fetched ahead of time. Defaults to <c>0</c> (no prefetch): a prefetched
    /// message holds a lock while it waits, and ingestion is slow enough that a large prefetch
    /// mostly buys lock expiries.
    /// </summary>
    public int PrefetchCount { get; set; }

    /// <summary>
    /// How long the SDK keeps renewing a message (and session) lock while ingestion runs.
    /// Defaults to five minutes — long enough for a large document, bounded so a wedged
    /// handler cannot hold a message forever.
    /// </summary>
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);
}
