namespace Rag.NET.Diagnostics;

/// <summary>
/// A disposable record of one query execution, kept in memory so a developer can answer
/// <i>"why did this give a bad answer"</i>.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> an audit trail. <c>IAuditLog</c> in <c>Rag.NET.Security</c> is the
/// compliance-grade record: it must not lose events, it is retained, and it is written for someone
/// who may later have to prove what happened. A trace is the opposite by construction — the last N
/// executions, in process memory, dropped on restart — and is read by a developer five minutes after
/// the request. The two share a vocabulary and nothing else: <see cref="TraceChunk"/> uses
/// <c>AuditChunkRef</c>'s field names, and <c>Capture*Text</c> here answers <c>Log*Text</c> there.
/// The types are not shared, and neither is the assembly — see <see cref="TraceChunk"/> for what
/// that reference would have cost.
/// </para>
/// <para>
/// Everything textual is <see langword="null"/> unless the matching <c>Capture*</c> flag on
/// <see cref="RagTraceOptions"/> was turned on. The structural fields — ids, scores, counts,
/// latencies — are always present, and are what capture gives you by default.
/// </para>
/// <para>
/// Captured content is deliberately <b>not</b> re-sanitised, so a trace may hold text the pipeline
/// itself later removed. That is the point: the most common reason to open a trace is to see what a
/// sanitiser or guard did. It is also a reason to leave content capture off in production, which is
/// the default.
/// </para>
/// </remarks>
public sealed record RagTrace
{
    /// <summary>
    /// The <c>System.Diagnostics.Activity</c> trace id this execution ran under, and the key every
    /// part of the trace is joined on.
    /// </summary>
    public required string TraceId { get; init; }

    /// <summary>When the execution began.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// A stable hash of the query. Always present, including when
    /// <see cref="RagTraceOptions.CaptureQueryText"/> is off — it identifies repeated questions and
    /// correlates traces without retaining what anybody asked.
    /// </summary>
    public required string QueryHash { get; init; }

    /// <summary>
    /// The query as the user typed it. <see langword="null"/> unless
    /// <see cref="RagTraceOptions.CaptureQueryText"/> is <see langword="true"/>.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Which chunks retrieval returned, what they scored, and — only under
    /// <see cref="RagTraceOptions.CaptureChunkText"/> — what they said. The list itself is always
    /// present: which chunks came back is structure, not content.
    /// </summary>
    /// <remarks>
    /// Each entry carries its own text, so text can never be read against the wrong chunk. See
    /// <see cref="TraceChunk"/> for why that is one record rather than a second list running
    /// alongside this one.
    /// </remarks>
    public IReadOnlyList<TraceChunk> Chunks { get; init; } = [];

    /// <summary>
    /// Every guard and sanitiser that ran, in order, and what each one removed or rewrote.
    /// </summary>
    public IReadOnlyList<TraceGuardAction> GuardActions { get; init; } = [];

    /// <summary>The pipeline stages that ran, with their latencies.</summary>
    public IReadOnlyList<TraceStage> Stages { get; init; } = [];

    /// <summary>
    /// The prompt the answer engine was actually given. <see langword="null"/> unless
    /// <see cref="RagTraceOptions.CapturePromptText"/> is <see langword="true"/>.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// The generated answer. <see langword="null"/> unless
    /// <see cref="RagTraceOptions.CaptureAnswerText"/> is <see langword="true"/>.
    /// </summary>
    public string? Answer { get; init; }
}
