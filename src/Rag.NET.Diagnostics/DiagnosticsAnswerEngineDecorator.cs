using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Diagnostics;

/// <summary>
/// Wraps an <see cref="IAnswerEngine"/> and records the generated answer into the trace for the
/// current execution.
/// </summary>
/// <remarks>
/// <para>
/// Shaped after <c>AuditAnswerEngineDecorator</c> in <c>Rag.NET.Security</c>: the answer is recorded
/// after the inner engine returns, capture failures are logged rather than thrown, and the response is
/// handed back whatever happened.
/// </para>
/// <para>
/// <b>The streaming overload passes straight through and records nothing</b>, which is what the audit
/// decorator does too. Recording a streamed answer means buffering the whole thing before the caller
/// has finished reading it, so an observer would change the memory profile of the stream it is
/// observing. The prompt seam covers streaming instead — see <c>IPromptObserver</c>, which
/// <c>ChatAnswerEngine</c> calls on both paths — so a streamed execution still traces what the model
/// was asked, just not what it replied. That cover is conditional: see
/// <see cref="Internal.TracePromptObserver"/> for why a streamed prompt only joins when the host
/// supplies an ambient activity. A streamed trace always holds its chunks and stage latencies.
/// </para>
/// <para>
/// The answer text is passed through unredacted and untruncated; the collector owns the content gate.
/// See <see cref="RagTraceOptions.CaptureAnswerText"/> for what is kept.
/// </para>
/// </remarks>
public sealed partial class DiagnosticsAnswerEngineDecorator(
    IAnswerEngine inner,
    ITraceCollector collector,
    ILogger<DiagnosticsAnswerEngineDecorator>? logger = null) : IAnswerEngine
{
    private readonly ILogger<DiagnosticsAnswerEngineDecorator> _logger =
        logger ?? NullLogger<DiagnosticsAnswerEngineDecorator>.Instance;

    /// <inheritdoc/>
    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await inner.AskAsync(query, sources, options, cancellationToken).ConfigureAwait(false);

        try
        {
            var traceId = TraceCorrelation.CurrentTraceId();

            if (traceId is not null)
                collector.RecordAnswer(traceId, response.Answer);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, ex);
        }

        return response;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.AskStreamingAsync(query, sources, options, cancellationToken);

    [LoggerMessage(
        EventId = 1912556956, EventName = "log_capture_failed",
        Level = LogLevel.Warning,
        Message = "DiagnosticsAnswerEngineDecorator failed to capture the answer into the trace. " +
                  "The answer was returned unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, Exception ex);
}
