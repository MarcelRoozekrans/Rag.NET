using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Turns the stage spans the pipeline already emits into the latency breakdown of a trace.
/// </summary>
/// <remarks>
/// <para>
/// All seven stages — <c>ragnet.ingest</c>, <c>parse</c>, <c>chunk</c>, <c>embed</c>, <c>store</c>,
/// <c>retrieve</c>, <c>ask</c> — are already instrumented, so the timings cost nothing beyond
/// subscribing. Nothing is re-measured and no new instrumentation is added: a second stopwatch beside
/// an existing span is two numbers that can disagree.
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

    /// <summary>Records one finished stage span.</summary>
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
            if (activity is null ||
                !activity.OperationName.StartsWith(StageNamePrefix, StringComparison.Ordinal))
            {
                return;
            }

            // The same key the retrieval behavior and answer decorator correlate on:
            // Activity.TraceId as 32 lowercase hex characters. Every part of a trace joins on it,
            // which is what the audit log gets from its own generated RequestId.
            _collector.RecordStage(
                activity.TraceId.ToHexString(),
                new TraceStage
                {
                    Name = activity.OperationName,

                    // Spelled out rather than left to the implicit conversion (MA0132): the offset
                    // is zero because StartTimeUtc already is UTC, not because the local zone is.
                    StartedAt = new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
                    Duration = activity.Duration,
                });
        }
        catch (Exception ex)
        {
            LogStageCaptureFailed(_logger, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record a pipeline stage span into the trace. The pipeline is unaffected.")]
    private static partial void LogStageCaptureFailed(ILogger logger, Exception ex);
}
