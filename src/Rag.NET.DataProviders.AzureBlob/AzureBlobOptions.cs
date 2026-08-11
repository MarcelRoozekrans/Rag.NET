using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.AzureBlob;

/// <summary>Configuration for <see cref="AzureBlobDataProvider"/>.</summary>
public sealed class AzureBlobOptions : CloudStorageOptions
{
    /// <summary>Blob name prefix to filter enumeration (e.g. <c>"docs/"</c>). <c>null</c> = entire container.</summary>
    public string? Prefix { get; set; }
}
