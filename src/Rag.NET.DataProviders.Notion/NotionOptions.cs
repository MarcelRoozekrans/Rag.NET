using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Notion;

/// <summary>Configuration for <see cref="NotionDataProvider"/>.</summary>
public sealed class NotionOptions : CloudStorageOptions
{
    /// <summary>
    /// Scopes ingestion to a single Notion database. <see langword="null"/> — the default —
    /// enumerates every page the integration can see, through <c>POST /v1/search</c>.
    /// <para>
    /// When set, the provider queries <c>POST /v1/databases/{DatabaseId}/query</c> instead.
    /// That is not a filter applied afterwards: <c>/v1/search</c> accepts no <c>database_id</c>
    /// filter at all, which is why this property was documented as reserved and read by nothing
    /// until the second endpoint existed (issue #108).
    /// </para>
    /// <para>
    /// Scoping also makes <c>database_id</c> an honest metadata key. Every page a database query
    /// returns is by construction a page of that database, so the tag is true of every document
    /// it is written to — which it would not be under <c>/v1/search</c>, where results carry no
    /// parent object.
    /// </para>
    /// </summary>
    public string? DatabaseId { get; set; }
}
