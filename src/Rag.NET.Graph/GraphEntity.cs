namespace Rag.NET.Graph;

/// <summary>A named entity extracted from text with a type and description.</summary>
public sealed record GraphEntity(string Name, string Type, string Description)
{
    /// <summary>PageRank score computed over the entity-relationship graph.</summary>
    public double PageRankScore { get; set; }

    /// <summary>Document ID this entity was extracted from.</summary>
    public string? SourceDocumentId { get; init; }

    /// <summary>Chunk indices that mention this entity.</summary>
    public IReadOnlyList<string> SourceChunkIds { get; init; } = [];
}
