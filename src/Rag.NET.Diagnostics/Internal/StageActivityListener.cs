using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Turns the stage spans the pipeline already emits into the latency breakdown of a trace, and
/// decides when a trace is finished.
/// </summary>
/// <remarks>
/// <para>
/// All seven stages — <c>ragnet.ingest</c>, <c>parse</c>, <c>chunk</c>, <c>embed</c>, <c>store</c>,
/// <c>retrieve</c>, <c>ask</c> — are already instrumented, so the timings cost nothing beyond
/// subscribing. Nothing is re-measured and no new instrumentation is added: a second stopwatch beside
/// an existing span is two numbers that can disagree.
/// </para>
/// <para>
/// <b>This is also what calls <see cref="ITraceCollector.Commit"/></b>, and therefore the only reason
/// a trace ever reaches the ring buffer. The rule is <i>commit when the outermost pipeline span
/// stops</i>: a <c>ragnet.*</c> span whose parent is not itself a <c>ragnet.*</c> span from this
/// source is the last one of its execution, so the trace it belongs to is complete. Nothing else in
/// the capture path can make that call — the retrieval behavior does not know whether an answer is
/// coming, and the answer decorator never runs at all for a retrieve-only query.
/// </para>
/// <para>
/// The rule reads the same in every host, which is why it is expressed against the parent rather than
/// against a span name. Under ASP.NET the outermost <c>ragnet.query</c> hangs off the request
/// activity — not a pipeline span, so it commits. In a console app it is a root — no parent at all, so
/// it commits. A <c>RetrieveAsync</c> with no ask around it makes <c>ragnet.retrieve</c> the outermost
/// span, so it commits and leaves nothing in flight. And a <c>ragnet.retrieve</c> nested inside a
/// <c>ragnet.query</c>, or the recursive retrievals multi-query and deep-research issue, have a
/// pipeline span for a parent and so commit nothing early.
/// </para>
/// <para>
/// <b>This subscription changes sampling.</b> A listener returning <c>AllData</c> makes the pipeline's
/// spans be created even when no exporter is configured, which is the price of getting durations at
/// all — an unsampled <c>StartActivity</c> returns <see langword="null"/> and there is nothing to
/// time. It only affects the <c>Rag.NET</c> source, and only while diagnostics is registered.
/// </para>
/// </remarks>
internal sealed partial class StageActivityListener : IDisposable
{
    /// <summary>The name of the <see cref="ActivitySource"/> the pipeline's stage spans come from.</summary>
    /// <remarks>
    /// Hard-coded rather than referenced. The real declaration is
    /// <c>RagTelemetry.SourceName</c> in <c>src/Rag.NET/Telemetry/RagTelemetry.cs</c>, and it is
    /// <see langword="internal"/> — deliberately, because the OTel surface is a public commitment that
    /// belongs to its own phase, and widening a telemetry constant so one listener can read it starts
    /// making that commitment early and by accident. Subscribing by name is what every other consumer
    /// of an <see cref="ActivitySource"/> does; it is the source's public identity either way.
    /// </remarks>
    private const string SourceName = "Rag.NET";

    /// <summary>
    /// The prefix every pipeline stage span shares, and the filter that keeps anything else out.
    /// </summary>
    /// <remarks>
    /// The source is shared. Filtering by name as well means a span that later starts being emitted
    /// under <c>Rag.NET</c> for something that is not a stage does not silently appear in traces as
    /// though it were one.
    /// </remarks>
    private const string StageNamePrefix = "ragnet.";

    private readonly ITraceCollector _collector;
    private readonly ILogger<StageActivityListener> _logger;
    private readonly ActivityListener _listener;

    /// <summary>Subscribes to the pipeline's spans and records them into <paramref name="collector"/>.</summary>
    /// <param name="collector">Where stage timings go.</param>
    /// <param name="logger">Where subscription failures go. Optional.</param>
    public StageActivityListener(ITraceCollector collector, ILogger<StageActivityListener>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(collector);

        _collector = collector;
        _logger = logger ?? NullLogger<StageActivityListener>.Instance;

        _listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, SourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnStopped,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Unsubscribes. Spans stopped afterwards are not recorded.</summary>
    public void Dispose() => _listener.Dispose();

    /// <summary>Records one finished stage span, and commits the trace when it was the last one.</summary>
    /// <param name="activity">The span, already stopped, so its duration is final.</param>
    /// <remarks>
    /// This runs inside the pipeline's <c>Activity.Stop()</c>, on the thread that ran the stage, so
    /// anything thrown here would surface as a failed query. The collector already swallows its own
    /// failures; this catch covers the handful of lines before it.
    /// </remarks>
    private void OnStopped(Activity activity)
    {
        try
        {
            if (!IsPipelineSpan(activity))
                return;

            // The same key the retrieval behavior and answer decorator correlate on:
            // Activity.TraceId as 32 lowercase hex characters. Every part of a trace joins on it,
            // which is what the audit log gets from its own generated RequestId.
            var traceId = activity.TraceId.ToHexString();

            _collector.RecordStage(
                traceId,
                new TraceStage
                {
                    Name = activity.OperationName,

                    // Spelled out rather than left to the implicit conversion (MA0132): the offset
                    // is zero because StartTimeUtc already is UTC, not because the local zone is.
                    StartedAt = new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
                    Duration = activity.Duration,
                });

            // Committed after the stage is recorded, never before: this span's own latency belongs
            // in the trace it is closing, and one callback keeps that order guaranteed rather than
            // dependent on the order two listeners happened to subscribe in.
            if (!IsPipelineSpan(activity.Parent))
                _collector.Commit(traceId);
        }
        catch (Exception ex)
        {
            LogStageCaptureFailed(_logger, ex);
        }
    }

    /// <summary>Whether an activity is one of the pipeline's own stage spans.</summary>
    /// <param name="activity">The activity to classify. <see langword="null"/> is not one.</param>
    /// <returns><see langword="true"/> for a <c>ragnet.*</c> span from the <c>Rag.NET</c> source.</returns>
    /// <remarks>
    /// Applied to the stopped span and to its parent, which is what makes the commit rule work.
    /// <see cref="ActivityListener.ShouldListenTo"/> has already filtered the stopped span by source,
    /// but a parent can come from anywhere — under ASP.NET it is the request activity — so the source
    /// is checked here rather than assumed.
    /// </remarks>
    private static bool IsPipelineSpan([NotNullWhen(true)] Activity? activity) =>
        activity is not null
        && string.Equals(activity.Source.Name, SourceName, StringComparison.Ordinal)
        && activity.OperationName.StartsWith(StageNamePrefix, StringComparison.Ordinal);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record a pipeline stage span into the trace. The pipeline is unaffected.")]
    private static partial void LogStageCaptureFailed(ILogger logger, Exception ex);
}
