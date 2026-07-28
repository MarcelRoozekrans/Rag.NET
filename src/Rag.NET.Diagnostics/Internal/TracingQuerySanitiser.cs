using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Wraps one <see cref="IQuerySanitiser"/> and records how it rewrote the question.
/// </summary>
/// <remarks>
/// <para>
/// A query sanitiser can change what was searched for without anything downstream showing that it
/// did, which turns <i>"why did this find nothing"</i> into guesswork. The recorded action makes the
/// rewrite visible: the counts are characters in and characters out, and <c>Changed</c> is the real
/// signal, because a sanitiser that swaps one word for another of the same length changes everything
/// while both counts stay equal.
/// </para>
/// <para>
/// <b>Its text is the user's raw question, so it is gated on
/// <see cref="RagTraceOptions.CaptureQueryText"/></b> and not on
/// <see cref="RagTraceOptions.CaptureChunkText"/>. Reading the chunk flag here — which the collector
/// did for every guard action before <see cref="TraceContentKind"/> existed — would have meant
/// turning on chunk capture silently started retaining user questions.
/// </para>
/// </remarks>
internal sealed partial class TracingQuerySanitiser : IQuerySanitiser
{
    private readonly IQuerySanitiser _inner;
    private readonly ITraceCollector _collector;
    private readonly ILogger<TracingQuerySanitiser> _logger;
    private readonly string _component;

    /// <summary>Wraps <paramref name="inner"/> so its rewrites are recorded.</summary>
    /// <param name="inner">The sanitiser being observed.</param>
    /// <param name="collector">Where the action is recorded.</param>
    /// <param name="logger">Where capture failures go. Optional.</param>
    /// <remarks>
    /// Takes no <see cref="RagTraceOptions"/>, unlike <see cref="TracingRetrievalGuard"/>: the two
    /// strings it records already exist, so there is no work to skip when capture is off and no
    /// reason to read the flags twice.
    /// </remarks>
    public TracingQuerySanitiser(
        IQuerySanitiser inner,
        ITraceCollector collector,
        ILogger<TracingQuerySanitiser>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(collector);

        _inner = inner;
        _collector = collector;
        _logger = logger ?? NullLogger<TracingQuerySanitiser>.Instance;
        _component = inner.GetType().Name;
    }

    /// <inheritdoc/>
    public string Sanitise(string query)
    {
        var sanitised = _inner.Sanitise(query);

        try
        {
            Record(query, sanitised);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, _component, ex);
        }

        return sanitised;
    }

    /// <summary>Files the question as typed against the question as searched for.</summary>
    /// <param name="before">The query handed to the sanitiser.</param>
    /// <param name="after">The query it returned.</param>
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
            TraceContentKind.Query);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record how {Component} rewrote the query. " +
                  "The sanitised query stands and the pipeline is unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, string component, Exception ex);
}
