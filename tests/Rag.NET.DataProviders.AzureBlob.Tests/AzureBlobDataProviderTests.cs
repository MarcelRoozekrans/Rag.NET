using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.AzureBlob;
using Xunit;

namespace Rag.NET.DataProviders.AzureBlob.Tests;

public sealed class AzureBlobDataProviderTests
{
    [Fact]
    public void Constructor_NullContainer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AzureBlobDataProvider(null!));
    }

    [Fact]
    public void Constructor_WithOptions_StoresOptions()
    {
        var container = new BlobContainerClient(
            "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=dGVzdA==;EndpointSuffix=core.windows.net",
            "my-container");
        var options = new AzureBlobOptions { Prefix = "docs/", Extensions = [".md"] };

        var sut = new AzureBlobDataProvider(container, options);

        Assert.NotNull(sut);
    }

    [Fact]
    public void AddAzureBlobDataProvider_ConnectionString_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();

        services.AddAzureBlobDataProvider(
            "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=dGVzdA==;EndpointSuffix=core.windows.net",
            "my-container");

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<AzureBlobDataProvider>(provider);
    }

    [Fact]
    public void AddAzureBlobDataProvider_NullConnectionString_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddAzureBlobDataProvider(null!, "container"));
    }

    [Fact]
    public void AddAzureBlobDataProvider_NullContainerName_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsAny<ArgumentException>(() =>
            services.AddAzureBlobDataProvider("connstr", null!));
    }
}
