using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

public sealed class NotionOptions : CloudStorageOptions
{
    public string? DatabaseId { get; init; }
}
