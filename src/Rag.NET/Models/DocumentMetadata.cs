namespace Rag.NET.Models;

public sealed record DocumentMetadata
{
    public required string DocumentId { get; init; }
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
