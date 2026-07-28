using Rag.NET.Security;

namespace Rag.NET.Diagnostics;

/// <summary>One chunk retrieval returned, and — only under content capture — what it said.</summary>
/// <remarks>
/// <para>
/// The reference and its text are held <b>together</b> rather than in two positionally-aligned lists.
/// Two lists that must stay in step can drift, and in a debugger chunk text attached to the wrong
/// chunk is worse than no text at all: it sends you off to debug a chunk that was never involved.
/// Composition makes that misalignment unrepresentable.
/// </para>
/// <para>
/// <see cref="Chunk"/> is the audit package's <see cref="AuditChunkRef"/>, reused rather than
/// redefined — the document id, chunk index and score mean the same thing in both subsystems, and
/// <see cref="AuditChunkRef"/> deliberately carries no text, which is what makes <see cref="Text"/>
/// a separate, gated field here instead of a fifth property over there.
/// </para>
/// </remarks>
public sealed record TraceChunk
{
    /// <summary>Which chunk it was, and what it scored. Always present — this is structure.</summary>
    public required AuditChunkRef Chunk { get; init; }

    /// <summary>
    /// The chunk's body text. <see langword="null"/> unless
    /// <see cref="RagTraceOptions.CaptureChunkText"/> is <see langword="true"/>, and truncated to
    /// <see cref="RagTraceOptions.MaxCapturedCharacters"/> — visibly, see
    /// <see cref="RagTraceOptions.TruncationMarker"/> — when it is.
    /// </summary>
    public string? Text { get; init; }
}
