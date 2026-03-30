namespace Rag.NET.Graph;

/// <summary>Complete snapshot of the graph — entities, relationships, and communities.</summary>
public sealed record GraphSnapshot(
    IReadOnlyList<GraphEntity> Entities,
    IReadOnlyList<GraphRelationship> Relationships,
    IReadOnlyList<Community> Communities);
