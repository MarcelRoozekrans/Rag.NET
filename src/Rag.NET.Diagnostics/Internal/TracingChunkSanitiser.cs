using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Wraps one <see cref="IChunkSanitiser"/> and records how it rewrote a chunk.
/// </summary>
/// <remarks>
/// <para>
/// <c>PiiChunkSanitiser</c> and <c>RegexChunkSanitiser</c> rewrite chunk text in place, so the counts
/// alone cannot show that they fired — <c>Changed</c> is the signal, and it is why that property
/// exists separately from comparing <c>InputCount</c> to <c>OutputCount</c>.
/// </para>
/// <para>
/// <b>This runs at ingestion, not at query time.</b> <c>ChunkSanitiserBehavior</c> is an ingestion
/// behavior, so the actions recorded here land in whatever trace the ingestion spans are running
/// under rather than in a query's. That is still the answer to <i>"why does this chunk say
/// [REDACTED]"</i>; it just is not found by looking up the query that surfaced it.
/// </para>
/// <para>
/// Its text is document text, so it is gated on <see cref="RagTraceOptions.CaptureChunkText"/>.
/// </para>
/// </remarks>
internal sealed partial class TracingChunkSanitiser : IChunkSanitiser
{
    private readonly IChunkSanitiser _inner;
    private readonly ITraceCollector _collector;
    private readonly ILogger<TracingChunkSanitiser> _logger;
    private readonly string _component;

    /// <summary>Wraps <paramref name="inner"/> so its rewrites are recorded.</summary>
    /// <param name="inner">The sanitiser being observed.</param>
    /// <param name="collector">Where the action is recorded.</param>
    /// <param name="logger">Where capture failures go. Optional.</param>
    public TracingChunkSanitiser(
        IChunkSanitiser inner,
        ITraceCollector collector,
        ILogger<TracingChunkSanitiser>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(collector);

        _inner = inner;
        _collector = collector;
        _logger = logger ?? NullLogger<TracingChunkSanitiser>.Instance;
        _component = inner.GetType().Name;
    }

    /// <inheritdoc/>
    public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata)
    {
        var sanitised = _inner.Sanitise(text, metadata);

        try
        {
            Record(text, sanitised);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, _component, ex);
        }

        return sanitised;
    }

    /// <summary>Files the chunk as stored against the chunk as rewritten.</summary>
    /// <param name="before">The text handed to the sanitiser.</param>
    /// <param name="after">The text it returned.</param>
    private void Record(string? before, string? after)
    {
        var traceId = TraceCorrelation.CurrentTraceId();

        if (traceId is null)
            return;

        _collector.RecordGuardAction(
            traceId,
            new TraceGuardAction
            {
                Component = _component,
                InputCount = before?.Length ?? 0,
                OutputCount = after?.Length ?? 0,
                Changed = !string.Equals(before, after, StringComparison.Ordinal),
                InputText = before,
                OutputText = after,
            },
            TraceContentKind.Chunk);
    }

    [LoggerMessage(
        EventId = 1912556956, EventName = "log_capture_failed",
        Level = LogLevel.Warning,
        Message = "Failed to record how {Component} rewrote a chunk. " +
                  "The sanitised chunk stands and the pipeline is unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, string component, Exception ex);
}
