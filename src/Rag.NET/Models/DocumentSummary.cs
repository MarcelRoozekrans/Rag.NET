namespace Rag.NET.Models;

public sealed record DocumentSummary
{
    public required string DocumentId  { get; init; }
    public required string FileName    { get; init; }
    public string?         ContentType { get; init; }
    public required int    ChunkCount  { get; init; }
    public required DateTimeOffset IngestedAt { get; init; }
    public IDictionary<string, string> Tags { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
