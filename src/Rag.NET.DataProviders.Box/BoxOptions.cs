using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Box;

/// <summary>Configuration for <see cref="BoxDataProvider"/>.</summary>
public sealed class BoxOptions : CloudStorageOptions
{
    /// <summary>Box folder ID to enumerate. <c>"0"</c> = root.</summary>
    public string RootFolderId { get; set; } = "0";
}
