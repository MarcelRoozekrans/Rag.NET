namespace Rag.NET.Api.Contracts;

public sealed record IngestRequest
{
    public required string Content { get; init; }
    public string? DocumentId { get; init; }
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public IDictionary<string, string>? Tags { get; init; }
}
