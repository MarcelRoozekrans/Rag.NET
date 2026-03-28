using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Confluence;

public sealed class ConfluenceOptions : CloudStorageOptions
{
    public required string BaseUrl { get; init; }
    public required string Email   { get; init; }
    public string? SpaceKey        { get; init; }
}
