using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Rag.NET.Retrieval;

namespace Rag.NET.Diagnostics;

/// <summary>
/// Retrieval pipeline behavior that records the query and the chunks retrieval returned into the
/// trace for the current execution.
/// </summary>
/// <remarks>
/// <para>
/// Shaped after <c>AuditRetrievalBehavior</c> in <c>Rag.NET.Security</c>, which does the same job for
/// the compliance record: capture happens <b>after</b> <c>next</c>, capture failures are swallowed and
/// logged, and the results are returned whatever happened. A debugger that breaks the pipeline it
/// observes is worse than no debugger, so that line is held harder here — the audit behavior at least
/// has results to return after a failed write, and this one has nothing to return at all.
/// </para>
/// <para>
/// It correlates on <c>Activity.Current.TraceId</c> rather than on a generated request id kept in a
/// scoped service. See <see cref="TraceCorrelation"/> for why, and for what happens when there is no
/// ambient activity: nothing, silently.
/// </para>
/// <para>
/// <b>Position matters.</b> The behavior runs inside the <c>ragnet.retrieve</c> span that
/// <c>PipelineRetriever</c> opens around the behavior pipeline, so the trace id it reads is the one
/// <see cref="Internal.StageActivityListener"/> files that stage's latency under. A decorator over
/// <c>IRetriever</c> would sit outside that span and read whatever ambient activity the host happened
/// to provide — or none.
/// </para>
/// <para>
/// Chunk text is passed through as retrieval had it, unredacted and untruncated. The collector owns
/// the content gate; see <see cref="RagTraceOptions.CaptureChunkText"/> for what is kept.
/// </para>
/// </remarks>
public sealed partial class DiagnosticsRetrievalBehavior(
    ITraceCollector collector,
    ILogger<DiagnosticsRetrievalBehavior>? logger = null) : IRetrievalBehavior
{
    private readonly ILogger<DiagnosticsRetrievalBehavior> _logger =
        logger ?? NullLogger<DiagnosticsRetrievalBehavior>.Instance;

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx,
        CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        var results = await next(ctx, ct).ConfigureAwait(false);

        try
        {
            Capture(ctx, results);
        }
        catch (Exception ex)
        {
            // Deliberately not re-throwing OperationCanceledException the way the audit behavior
            // does: capture is synchronous and takes no token, so a cancellation surfacing here
            // would be somebody else's, and swallowing it is still better than failing the query.
            LogCaptureFailed(_logger, ex);
        }

        return results;
    }

    /// <summary>Files the query and the retrieved chunks under the current trace.</summary>
    /// <param name="ctx">The retrieval context, for the query text.</param>
    /// <param name="results">What retrieval returned.</param>
    private void Capture(RetrievalContext ctx, IReadOnlyList<SearchResult> results)
    {
        var traceId = TraceCorrelation.CurrentTraceId();

        if (traceId is null)
            return;

        collector.RecordQuery(traceId, ctx.Query);

        var chunks = new List<TraceChunk>(results.Count);

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];

            // The same three fields AuditChunkRef carries, under the same names, plus the text the
            // audit record deliberately never holds. The chunk's own text, not SearchResult's
            // CompressedText: the answer engine prefers the compressed view, but a trace is read to
            // find out what the chunk actually said, and substituting one for the other silently
            // would answer a question nobody asked.
            chunks.Add(new TraceChunk
            {
                DocumentId = result.Chunk.DocumentId.Value,
                ChunkIndex = result.Chunk.ChunkIndex,
                Score = result.Score,
                Text = result.Chunk.Text,
            });
        }

        collector.RecordChunks(traceId, chunks);
    }

    [LoggerMessage(
        EventId = 1912556956, EventName = "log_capture_failed",
        Level = LogLevel.Warning,
        Message = "DiagnosticsRetrievalBehavior failed to capture retrieval into the trace. " +
                  "The retrieval results were returned unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, Exception ex);
}
