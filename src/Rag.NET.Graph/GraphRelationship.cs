namespace Rag.NET.Graph;

/// <summary>A directed relationship between two entities.</summary>
public sealed record GraphRelationship(
    string SourceEntity,
    string TargetEntity,
    string Description,
    double Weight = 1.0)
{
    /// <summary>Document ID this relationship was extracted from.</summary>
    public string? SourceDocumentId { get; init; }

    /// <summary>Chunk IDs this relationship was extracted from.</summary>
    /// <remarks>
    /// <para>
    /// The same <c>{DocumentId}_{ChunkIndex}</c> form <see cref="GraphEntity.SourceChunkIds"/>
    /// uses, and it exists for the same reason at one remove: local search orders the source chunks
    /// it puts in front of the model by how many of the seed entity's relationships came from each
    /// one. Without this the tie-break has nothing to read, and chunks within one entity's block
    /// fall back to store order.
    /// </para>
    /// <para>
    /// Empty on every relationship written before this property existed, and on any store that does
    /// not persist it. That is a degradation in ordering, not in correctness — see
    /// <c>SourceChunkSelection.CountRelationships</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> SourceChunkIds { get; init; } = [];
}
