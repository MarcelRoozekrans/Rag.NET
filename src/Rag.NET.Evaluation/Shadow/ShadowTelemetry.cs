using System.Diagnostics.Metrics;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// The shadow capture path's meter, mirroring core's <c>RagTelemetry</c> shape — an internal
/// static class of <c>ragnet.*</c>-named instruments — on Evaluation's own meter, because
/// <c>RagTelemetry</c> is internal to <c>Rag.NET</c> and this package must not depend on core.
/// </summary>
/// <remarks>
/// <para>
/// <b>The arithmetic these five exist for:</b>
/// <c>enqueued − dropped − failed − abandoned = persisted (processed)</c>.
/// <c>ragnet.shadow.enqueued</c> counts every sampled capture the request path handed to the
/// queue — so it tracks the configured sample rate — and the three loss counters are the entire
/// gap between that rate and what the store holds: <c>dropped</c> is backpressure (the queue was
/// full, or completed at shutdown), <c>failed</c> is captures the store or sanitiser refused
/// (each individually recorded in <c>ShadowCaptureConsumer.FailureSnapshot</c>), and
/// <c>abandoned</c> is what the shutdown drain deadline expired on. A dashboard whose
/// <c>processed</c> sits below <c>enqueued</c> with all three loss counters at zero is
/// measuring a consumer that has not caught up yet, not loss.
/// </para>
/// <para>
/// The counters are cumulative per process, like every <c>ragnet.*</c> instrument; the exact
/// per-instance figures remain on the objects themselves (<c>ShadowCaptureQueue.DroppedCount</c>,
/// <c>ShadowCaptureConsumer.AbandonedCount</c>).
/// </para>
/// </remarks>
internal static class ShadowTelemetry
{
    internal const string MeterName = "Rag.NET.Evaluation";

    internal static readonly Meter Meter = new(MeterName, "1.0.0");

    internal static readonly Counter<long> Enqueued =
        Meter.CreateCounter<long>("ragnet.shadow.enqueued", "captures",
            "Sampled captures handed to the shadow queue by the request path, accepted or not");

    internal static readonly Counter<long> Dropped =
        Meter.CreateCounter<long>("ragnet.shadow.dropped", "captures",
            "Sampled captures the queue dropped: full, or already completed by shutdown");

    internal static readonly Counter<long> Processed =
        Meter.CreateCounter<long>("ragnet.shadow.processed", "captures",
            "Captured pairs persisted to the store, secondary run included");

    internal static readonly Counter<long> Failed =
        Meter.CreateCounter<long>("ragnet.shadow.failed", "captures",
            "Captures lost because the store or sanitiser threw; each is also recorded individually");

    internal static readonly Counter<long> Abandoned =
        Meter.CreateCounter<long>("ragnet.shadow.abandoned", "captures",
            "Accepted captures still unpersisted when the shutdown drain deadline expired");
}
