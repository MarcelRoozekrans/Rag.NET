using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.GoogleDrive;

/// <summary>Configuration for <see cref="GoogleDriveDataProvider"/>.</summary>
public sealed class GoogleDriveOptions : CloudStorageOptions
{
    /// <summary>Google Drive folder ID to enumerate. <c>null</c> = entire drive.</summary>
    public string? FolderId { get; set; }
}
