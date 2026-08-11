using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.Dropbox;

/// <summary>Configuration for <see cref="DropboxDataProvider"/>.</summary>
public sealed class DropboxOptions : CloudStorageOptions
{
    /// <summary>Dropbox folder path (e.g. <c>"/docs"</c>). Empty string = root.</summary>
    public string FolderPath { get; set; } = string.Empty;
}
