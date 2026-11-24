namespace Rag.NET.Api.Contracts;

/// <summary>
/// The body of <c>POST {prefix}/ask</c> and <c>POST {prefix}/ask/stream</c>.
/// </summary>
public sealed record AskRequest
{
    /// <summary>The question to answer.</summary>
    public required string Query { get; init; }

    /// <summary>
    /// The per-call options, mirroring <c>RagOptions</c>. <see langword="null"/>, and every field
    /// left out, means the in-process <c>RagPipeline</c>'s default.
    /// </summary>
    public RagOptionsDto? Options { get; init; }
}
