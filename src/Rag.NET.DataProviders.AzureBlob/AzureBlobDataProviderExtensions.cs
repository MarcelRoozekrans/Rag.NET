using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;

namespace Rag.NET.DataProviders.AzureBlob;

/// <summary>DI registration extensions for <see cref="AzureBlobDataProvider"/>.</summary>
public static class AzureBlobDataProviderExtensions
{
    /// <summary>Registers <see cref="AzureBlobDataProvider"/> using a connection string.</summary>
    public static IServiceCollection AddAzureBlobDataProvider(
        this IServiceCollection services,
        string connectionString,
        string containerName,
        Action<AzureBlobOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var options = new AzureBlobOptions();
        configure?.Invoke(options);
        var container = new BlobContainerClient(connectionString, containerName);
        return services.AddSingleton<IFileContentProvider>(new AzureBlobDataProvider(container, options));
    }

    /// <summary>Registers <see cref="AzureBlobDataProvider"/> using a <see cref="TokenCredential"/> (OAuth / managed identity).</summary>
    public static IServiceCollection AddAzureBlobDataProvider(
        this IServiceCollection services,
        TokenCredential credential,
        Uri containerUri,
        Action<AzureBlobOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(containerUri);

        var options = new AzureBlobOptions();
        configure?.Invoke(options);
        var container = new BlobContainerClient(containerUri, credential);
        return services.AddSingleton<IFileContentProvider>(new AzureBlobDataProvider(container, options));
    }
}
