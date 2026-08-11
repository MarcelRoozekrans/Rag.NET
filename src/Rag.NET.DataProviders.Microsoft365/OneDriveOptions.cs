using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.OneDrive;

/// <summary>Configuration for <see cref="OneDriveDataProvider"/>.</summary>
public sealed class OneDriveOptions : CloudStorageOptions
{
    /// <summary>User ID or <c>"me"</c> for delegated auth.</summary>
    public required string UserId { get; set; }
}
