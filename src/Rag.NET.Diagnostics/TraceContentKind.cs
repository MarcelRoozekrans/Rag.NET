namespace Rag.NET.Diagnostics;

/// <summary>
/// Which kind of content a captured text field holds, and therefore which <c>Capture*</c> flag on
/// <see cref="RagTraceOptions"/> decides whether it is kept.
/// </summary>
/// <remarks>
/// <para>
/// Most of the recording surface needs no such thing: <c>RecordQuery</c> is always a query and
/// <c>RecordAnswer</c> is always an answer, so the flag is implied by the method. Guard actions are
/// the exception, and they are the reason this exists. One <c>TraceGuardAction</c> shape is produced
/// by three decorators over three different interfaces, and the text they carry is not the same kind
/// of thing: a <c>TracingQuerySanitiser</c>'s input <b>is the user's raw question</b>, while a
/// <c>TracingRetrievalGuard</c>'s is document text.
/// </para>
/// <para>
/// Gating both on <see cref="RagTraceOptions.CaptureChunkText"/> — which is what the collector did
/// before this type existed — meant that turning on chunk-text capture silently started retaining
/// user questions in memory with <see cref="RagTraceOptions.CaptureQueryText"/> still off. That is
/// exactly the silent content capture the whole options type exists to prevent, so the producer says
/// which kind it is producing and the collector picks the matching flag.
/// </para>
/// </remarks>
public enum TraceContentKind
{
    /// <summary>The user's question. Governed by <see cref="RagTraceOptions.CaptureQueryText"/>.</summary>
    Query = 0,

    /// <summary>Retrieved document text. Governed by <see cref="RagTraceOptions.CaptureChunkText"/>.</summary>
    Chunk = 1,

    /// <summary>The assembled prompt. Governed by <see cref="RagTraceOptions.CapturePromptText"/>.</summary>
    Prompt = 2,

    /// <summary>The generated answer. Governed by <see cref="RagTraceOptions.CaptureAnswerText"/>.</summary>
    Answer = 3,
}
