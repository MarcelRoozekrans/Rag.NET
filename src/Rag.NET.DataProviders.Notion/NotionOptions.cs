using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

/// <summary>Configuration for <see cref="NotionDataProvider"/>.</summary>
public sealed class NotionOptions : CloudStorageOptions
{
    /// <summary>
    /// Reserved for a future implementation that will scope results to a specific database
    /// by querying <c>POST /v1/databases/{DatabaseId}/query</c> instead of <c>/v1/search</c>.
    /// The <c>/v1/search</c> endpoint used today does not accept a database_id filter.
    /// </summary>
    public string? DatabaseId { get; init; }
}
