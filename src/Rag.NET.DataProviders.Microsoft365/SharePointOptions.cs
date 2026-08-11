using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.SharePoint;

/// <summary>Configuration for <see cref="SharePointDataProvider"/>.</summary>
public sealed class SharePointOptions : CloudStorageOptions
{
    /// <summary>SharePoint site ID.</summary>
    public required string SiteId { get; set; }

    /// <summary>Drive ID within the site.</summary>
    public required string DriveId { get; set; }
}
