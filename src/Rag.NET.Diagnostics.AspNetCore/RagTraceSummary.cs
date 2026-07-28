using Rag.NET.Diagnostics;

namespace Rag.NET.Diagnostics.AspNetCore;

/// <summary>One line per trace: enough to pick the one you want, and no captured text.</summary>
/// <remarks>
/// <para>
/// The list route returns these rather than whole traces, and the reason is arithmetic. A fully
/// capturing trace can hold <c>TopK</c> chunk texts, a prompt, an answer and two strings per guard
/// action, each up to <see cref="RagTraceOptions.MaxCapturedCharacters"/>; multiplied by
/// <see cref="RagTraceOptions.Capacity"/> that is megabytes of document text in one response, served
/// to anyone who asked for a list. Fetching one trace by id is a deliberate second step.
/// </para>
/// <para>
/// It also means the list route never returns captured content at all, whatever the <c>Capture*</c>
/// flags say — <see cref="QueryHash"/> is what distinguishes repeated questions here, which is exactly
/// the job it does inside <see cref="RagTrace"/> when text capture is off.
/// </para>
/// </remarks>
public sealed record RagTraceSummary
{
    /// <summary>The trace id, and the value to fetch the full trace with.</summary>
    public required string TraceId { get; init; }

    /// <summary>When the execution began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The query's hash. Identifies repeated questions without retaining any of them.</summary>
    public required string QueryHash { get; init; }

    /// <summary>How many chunks retrieval returned.</summary>
    public required int ChunkCount { get; init; }

    /// <summary>How many guards and sanitisers ran.</summary>
    public required int GuardActionCount { get; init; }

    /// <summary>How many pipeline stages the trace holds.</summary>
    public required int StageCount { get; init; }

    /// <summary>How long the execution took, in milliseconds.</summary>
    /// <remarks>
    /// The longest single stage, which is the outermost span — <c>ragnet.query</c> for an ask,
    /// <c>ragnet.retrieve</c> for a retrieve-only call — because every other stage ran inside it.
    /// Summing the stages instead would double-count the nesting.
    /// </remarks>
    public required double DurationMilliseconds { get; init; }

    /// <summary>Whether the trace holds any captured text at all.</summary>
    /// <remarks>
    /// So a reader can tell "the answer was empty" from "answer capture was off" without fetching the
    /// trace to find out that it holds nothing they wanted.
    /// </remarks>
    public required bool HasCapturedText { get; init; }

    /// <summary>Summarises one trace.</summary>
    /// <param name="trace">The trace to summarise.</param>
    /// <returns>Its one-line form.</returns>
    public static RagTraceSummary Of(RagTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        return new RagTraceSummary
        {
            TraceId = trace.TraceId,
            StartedAt = trace.StartedAt,
            QueryHash = trace.QueryHash,
            ChunkCount = trace.Chunks.Count,
            GuardActionCount = trace.GuardActions.Count,
            StageCount = trace.Stages.Count,
            DurationMilliseconds = LongestStageMilliseconds(trace),
            HasCapturedText = HasText(trace),
        };
    }

    /// <summary>The duration of the longest stage, which encloses the rest.</summary>
    /// <param name="trace">The trace to measure.</param>
    /// <returns>Milliseconds, or zero when no stage was recorded.</returns>
    private static double LongestStageMilliseconds(RagTrace trace)
    {
        var longest = 0d;

        for (var i = 0; i < trace.Stages.Count; i++)
        {
            var elapsed = trace.Stages[i].Duration.TotalMilliseconds;

            if (elapsed > longest)
                longest = elapsed;
        }

        return longest;
    }

    /// <summary>Whether any <c>Capture*</c> flag produced anything in this trace.</summary>
    /// <param name="trace">The trace to inspect.</param>
    /// <returns><see langword="true"/> when at least one text field is populated.</returns>
    private static bool HasText(RagTrace trace)
    {
        if (trace.Query is not null || trace.Prompt is not null || trace.Answer is not null)
            return true;

        for (var i = 0; i < trace.Chunks.Count; i++)
        {
            if (trace.Chunks[i].Text is not null)
                return true;
        }

        for (var i = 0; i < trace.GuardActions.Count; i++)
        {
            if (trace.GuardActions[i].InputText is not null || trace.GuardActions[i].OutputText is not null)
                return true;
        }

        return false;
    }
}
