using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Wraps one <see cref="IRetrievalGuard"/> and records that it ran, and what it removed.
/// </summary>
/// <remarks>
/// <para>
/// This is the diagnostic hole the phase exists to close. <c>RbacRetrievalGuard</c>,
/// <c>TrustLevelRetrievalGuard</c> and <c>RegexRetrievalGuard</c> all silently change what the
/// pipeline saw, and until now nothing anywhere recorded that one had fired — so <i>"why is that
/// chunk missing from the answer"</i> had no answer at all.
/// </para>
/// <para>
/// <b>A guard that changed nothing is still recorded.</b> <i>"The guard ran and let everything
/// through"</i> and <i>"the guard never ran"</i> are different answers to that question, and a
/// capture that omitted the first would leave someone debugging the wrong half of their
/// configuration.
/// </para>
/// </remarks>
internal sealed partial class TracingRetrievalGuard : IRetrievalGuard
{
    private readonly IRetrievalGuard _inner;
    private readonly ITraceCollector _collector;
    private readonly RagTraceOptions _options;
    private readonly ILogger<TracingRetrievalGuard> _logger;
    private readonly string _component;

    /// <summary>Wraps <paramref name="inner"/> so its effect on the results is recorded.</summary>
    /// <param name="inner">The guard whose decisions are being observed.</param>
    /// <param name="collector">Where the action is recorded.</param>
    /// <param name="options">
    /// Read only to decide whether joining the chunk text is work worth doing — <b>not</b> a second
    /// content gate. <see cref="ITraceCollector"/> remains the authority and would discard the text
    /// anyway; this check can therefore only cost a trace text it was entitled to, never leak text it
    /// was not. It is here because the join is the one piece of capture that has to <i>build</i>
    /// something: several kilobytes of document text per query, allocated and thrown away on the
    /// default settings, which would make "diagnostics captures structure" quietly expensive.
    /// </param>
    /// <param name="logger">Where capture failures go. Optional.</param>
    public TracingRetrievalGuard(
        IRetrievalGuard inner,
        ITraceCollector collector,
        RagTraceOptions options,
        ILogger<TracingRetrievalGuard>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _collector = collector;
        _options = options;
        _logger = logger ?? NullLogger<TracingRetrievalGuard>.Instance;
        _component = inner.GetType().Name;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results)
    {
        var inspected = _inner.Inspect(results);

        try
        {
            Record(results, inspected);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, _component, ex);
        }

        return inspected;
    }

    /// <summary>Files what the guard was given against what it returned.</summary>
    /// <param name="before">The results handed to the guard.</param>
    /// <param name="after">The results it returned.</param>
    private void Record(IReadOnlyList<SearchResult> before, IReadOnlyList<SearchResult> after)
    {
        var traceId = TraceCorrelation.CurrentTraceId();

        if (traceId is null)
            return;

        var wantsText = _options.CaptureChunkText;

        _collector.RecordGuardAction(
            traceId,
            new TraceGuardAction
            {
                Component = _component,
                InputCount = before?.Count ?? 0,
                OutputCount = after?.Count ?? 0,
                Changed = Changed(before, after),
                InputText = wantsText ? JoinTexts(before) : null,
                OutputText = wantsText ? JoinTexts(after) : null,
            },
            TraceContentKind.Chunk);
    }

    /// <summary>Whether the guard altered the list at all.</summary>
    /// <param name="before">The results handed to the guard.</param>
    /// <param name="after">The results it returned.</param>
    /// <returns><see langword="true"/> when anything was dropped, added or rewritten.</returns>
    /// <remarks>
    /// Reference equality per element, not value equality. A guard that redacts produces new
    /// <see cref="SearchResult"/> records, so a changed reference is exactly the signal; comparing by
    /// value would re-walk every chunk's text on every query to learn the same thing.
    /// </remarks>
    private static bool Changed(IReadOnlyList<SearchResult>? before, IReadOnlyList<SearchResult>? after)
    {
        if (ReferenceEquals(before, after))
            return false;

        if (before is null || after is null || before.Count != after.Count)
            return true;

        for (var i = 0; i < before.Count; i++)
        {
            if (!ReferenceEquals(before[i], after[i]))
                return true;
        }

        return false;
    }

    /// <summary>Concatenates the chunk texts so the two sides of the guard can be compared.</summary>
    /// <param name="results">The results to render.</param>
    /// <returns>Their texts, separated blank-line style.</returns>
    private static string JoinTexts(IReadOnlyList<SearchResult>? results)
    {
        if (results is null || results.Count == 0)
            return string.Empty;

        var texts = new string[results.Count];

        for (var i = 0; i < results.Count; i++)
            texts[i] = results[i]?.Chunk?.Text ?? string.Empty;

        return string.Join("\n\n", texts);
    }

    [LoggerMessage(
        EventId = 1912556956, EventName = "log_capture_failed",
        Level = LogLevel.Warning,
        Message = "Failed to record what {Component} did to the retrieval results. " +
                  "The guard's own decision stands and the pipeline is unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, string component, Exception ex);
}
