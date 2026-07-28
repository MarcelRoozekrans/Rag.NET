using Rag.NET.Security;

namespace Rag.NET.Diagnostics;

/// <summary>
/// A disposable record of one query execution, kept in memory so a developer can answer
/// <i>"why did this give a bad answer"</i>.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>not</b> an audit trail. <see cref="IAuditLog"/> is the compliance-grade record: it must
/// not lose events, it is retained, and it is written for someone who may later have to prove what
/// happened. A trace is the opposite by construction — the last N executions, in process memory,
/// dropped on restart — and is read by a developer five minutes after the request. The two share
/// types and vocabulary (<see cref="AuditChunkRef"/> here, <c>Log*Text</c> against
/// <c>Capture*Text</c> in the options) and nothing else.
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
    /// Which chunks retrieval returned, and what they scored. Always present — this is structure,
    /// not content.
    /// </summary>
    public IReadOnlyList<AuditChunkRef> Chunks { get; init; } = [];

    /// <summary>
    /// The chunks' text, positionally aligned with <see cref="Chunks"/>: <c>ChunkTexts[i]</c> is the
    /// text of <c>Chunks[i]</c>, and when non-<see langword="null"/> the two lists have the same
    /// length. <see langword="null"/> as a whole unless
    /// <see cref="RagTraceOptions.CaptureChunkText"/> is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="Chunks"/> rather than inside it so that <see cref="AuditChunkRef"/> is
    /// reused as-is. It carries no text deliberately, and the audit log depends on that.
    /// </remarks>
    public IReadOnlyList<string>? ChunkTexts { get; init; }

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
