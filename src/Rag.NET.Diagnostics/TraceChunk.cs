namespace Rag.NET.Diagnostics;

/// <summary>One chunk retrieval returned, and — only under content capture — what it said.</summary>
/// <remarks>
/// <para>
/// <b>The first three properties deliberately mirror <c>AuditChunkRef</c> in
/// <c>Rag.NET.Security</c>, name for name, without taking a dependency on it.</b> Sharing the type
/// would have been the DRYer option and it was measured rather than assumed: referencing
/// <c>Rag.NET.Security</c> pulls this package's transitive closure from 15 NuGet packages to 41,
/// dragging in SQLite and its native binaries, the ML tokenizers and their data file, Polly and
/// protobuf — for a package that is five records and a ring buffer, to reuse one three-property
/// record and make a few doc-comment links resolve. The names still match, so the vocabulary is
/// shared even though the assembly is not; that is what mattered about reuse here.
/// </para>
/// <para>
/// This is not a mirror that can silently drift out of step with a shared shape, because it already
/// diverges by design the moment it carries <see cref="Text"/> — <c>AuditChunkRef</c> holds no text
/// deliberately, and the audit log depends on it not doing so.
/// </para>
/// <para>
/// The text is held <b>on the chunk</b> rather than in a second list running alongside. Two lists
/// that must stay in step can drift, and in a debugger chunk text attached to the wrong chunk is
/// worse than no text at all: it sends you off to debug a chunk that was never involved.
/// </para>
/// </remarks>
public sealed record TraceChunk
{
    /// <summary>The document the chunk came from. Always present — this is structure.</summary>
    public required string DocumentId { get; init; }

    /// <summary>The chunk's position within that document.</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>What the chunk scored against the query.</summary>
    public required double Score { get; init; }

    /// <summary>
    /// The chunk's body text. <see langword="null"/> unless
    /// <see cref="RagTraceOptions.CaptureChunkText"/> is <see langword="true"/>, and truncated to
    /// <see cref="RagTraceOptions.MaxCapturedCharacters"/> — visibly, see
    /// <see cref="RagTraceOptions.TruncationMarker"/> — when it is.
    /// </summary>
    public string? Text { get; init; }
}
