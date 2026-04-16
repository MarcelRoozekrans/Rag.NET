namespace Rag.NET.Security;

/// <summary>A reference to a chunk that appeared in a retrieval result.</summary>
public sealed record AuditChunkRef
{
    public required string DocumentId { get; init; }
    public required int    ChunkIndex { get; init; }
    public required double Score      { get; init; }
}
