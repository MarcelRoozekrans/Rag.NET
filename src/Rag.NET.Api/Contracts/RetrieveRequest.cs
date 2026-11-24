namespace Rag.NET.Api.Contracts;

/// <summary>The body of <c>POST {prefix}/retrieve</c>.</summary>
public sealed record RetrieveRequest
{
    /// <summary>The query to retrieve chunks for.</summary>
    public required string Query { get; init; }

    /// <summary>
    /// The per-call options, mirroring <c>RetrievalOptions</c>. <see langword="null"/>, and every
    /// field left out, means the in-process pipeline's default.
    /// </summary>
    public RetrievalOptionsDto? Options { get; init; }
}
