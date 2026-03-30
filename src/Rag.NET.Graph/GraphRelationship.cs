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
}
