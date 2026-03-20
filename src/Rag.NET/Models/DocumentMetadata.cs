using ZeroAlloc.Validation;

namespace Rag.NET.Models;

[Validate]
public sealed class DocumentMetadata
{
    public required DocumentId DocumentId { get; init; }

    [NotEmpty]
    public required string FileName { get; init; }

    public string? ContentType { get; init; }
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
