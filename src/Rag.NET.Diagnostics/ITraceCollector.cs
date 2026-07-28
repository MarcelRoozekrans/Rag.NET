namespace Rag.NET.Diagnostics;

/// <summary>
/// Assembles one <see cref="RagTrace"/> per query from the several places that see part of it, and
/// commits it when the request ends.
/// </summary>
/// <remarks>
/// <para>
/// A trace is built by three or four unrelated components — a retrieval behavior, an answer
/// decorator, an <c>ActivityListener</c> over the stage spans, the guard and sanitiser decorators —
/// none of which knows about the others. They agree on one thing: the <c>Activity</c> trace id.
/// Every method here takes it, and everything recorded under the same id ends up in the same trace.
/// </para>
/// <para>
/// <b>Callers pass text as they have it, unredacted and untruncated.</b> The implementation owns the
/// content gate: it decides, in one place, whether a field is kept at all and how much of it. A
/// caller that pre-filtered would be a second gate, and a second gate is a second thing to get
/// wrong. See <see cref="RagTraceOptions"/> for what each flag retains.
/// </para>
/// <para>
/// <b>No method here throws.</b> Capture is an observer of the pipeline, and an observer that can
/// break what it observes is worse than no observer: a bad argument, an unknown trace id or a defect
/// inside the collector is swallowed and logged, never surfaced to the caller. This is the same line
/// <c>AuditRetrievalBehavior</c> holds, held harder — that one still returns its results after a
/// failed write; this one has nothing to return at all.
/// </para>
/// </remarks>
public interface ITraceCollector
{
    /// <summary>Records the query, and always its hash.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <param name="query">The query text, as the user typed it.</param>
    /// <remarks>
    /// <see cref="RagTrace.QueryHash"/> is written whatever
    /// <see cref="RagTraceOptions.CaptureQueryText"/> says; <see cref="RagTrace.Query"/> only when it
    /// is on. That is the distinction the whole options type exists to draw.
    /// </remarks>
    void RecordQuery(string traceId, string query);

    /// <summary>Records which chunks retrieval returned.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <param name="chunks">
    /// The chunks, each with its text already attached. The text is dropped or truncated here, so
    /// pass it as retrieval had it.
    /// </param>
    void RecordChunks(string traceId, IReadOnlyList<TraceChunk> chunks);

    /// <summary>Records that a guard or sanitiser ran, and what it did.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <param name="action">
    /// What the component was given and what it returned. The counts always survive; the text fields
    /// are gated on the flag <paramref name="contentKind"/> names.
    /// </param>
    /// <param name="contentKind">
    /// What sort of text this component handles, and so which <c>Capture*</c> flag governs it. A
    /// query sanitiser's input is the user's question and a retrieval guard's is document text; the
    /// caller is the only party that knows which, so it says. See <see cref="TraceContentKind"/>.
    /// </param>
    void RecordGuardAction(string traceId, TraceGuardAction action, TraceContentKind contentKind);

    /// <summary>Records that a pipeline stage ran, and how long it took.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <param name="stage">The stage span, already stopped.</param>
    void RecordStage(string traceId, TraceStage stage);

    /// <summary>Records the prompt the answer engine was given.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <param name="prompt">The assembled prompt.</param>
    void RecordPrompt(string traceId, string prompt);

    /// <summary>Records the generated answer.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <param name="answer">The answer text.</param>
    void RecordAnswer(string traceId, string answer);

    /// <summary>Reads a trace that is still being assembled.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <returns>
    /// A snapshot of the trace so far, or <see langword="null"/> if nothing has been recorded under
    /// this id or it has already been committed. Committed traces are read from the buffer instead.
    /// </returns>
    RagTrace? Current(string traceId);

    /// <summary>Finishes the trace and hands it to the ring buffer.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id this execution runs under.</param>
    /// <remarks>
    /// Committing an id nothing was recorded under does nothing, which is the normal outcome when
    /// diagnostics is registered but a request failed before it reached anything that records.
    /// </remarks>
    void Commit(string traceId);
}
