namespace Rag.NET.Models;

public sealed record DocumentSection
{
    public required string Text { get; init; }
    public required DocumentId DocumentId { get; init; }
    public int? HeadingLevel { get; init; }
    public string? Heading { get; init; }
    public int? PageNumber { get; init; }
    public int SectionIndex { get; init; }
}
