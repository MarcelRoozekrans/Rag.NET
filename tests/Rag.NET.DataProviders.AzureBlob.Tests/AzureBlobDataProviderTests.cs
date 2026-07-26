using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.AzureBlob;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.AzureBlob.Tests;

public sealed class AzureBlobDataProviderTests
{
    private const string ConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=dGVzdA==;EndpointSuffix=core.windows.net";

    /// <summary>
    /// Builds a container client whose transport returns a canned <c>ListBlobs</c> XML payload,
    /// so enumeration never touches the network.
    /// </summary>
    private static BlobContainerClient MakeContainer(string containerName, params string[] blobNames)
    {
        var blobs = new StringBuilder();
        foreach (var name in blobNames)
        {
            blobs.Append("<Blob><Name>").Append(name).Append("</Name>")
                 .Append("<Properties><Etag>0x8DAAAA1</Etag></Properties></Blob>");
        }

        var xml =
            $"""<?xml version="1.0" encoding="utf-8"?><EnumerationResults ServiceEndpoint="https://test.blob.core.windows.net/" ContainerName="{containerName}"><Blobs>{blobs}</Blobs><NextMarker /></EnumerationResults>""";

        var options = new BlobClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(new CannedXmlHandler(xml))),
        };
        return new BlobContainerClient(ConnectionString, containerName, options);
    }

    // -------------------------------------------------------------------------
    // Metadata — the exact keys and values this connector emits
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_EmitsPathAndContainerMetadata()
    {
        var sut = new AzureBlobDataProvider(MakeContainer("my-container", "docs/readme.md"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(entries).Value;
        Assert.NotNull(entry.Metadata);
        Assert.Equal("docs/readme.md", entry.Metadata["path"]);
        Assert.Equal("my-container",   entry.Metadata["container"]);
        // Exactly these two keys — nothing else is in hand at handle construction.
        Assert.Equal(2, entry.Metadata.Count);
        MetadataContract.AssertValid(entry);
    }

    [Fact]
    public async Task GetFilesAsync_AllEntriesSatisfyMetadataContract()
    {
        var sut = new AzureBlobDataProvider(
            MakeContainer("docs-container", "a.md", "nested/deep/b.md"));

        var entries = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, entries.Count);
        MetadataContract.AssertAll(entries.Select(e => e.Value));
    }

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

/// <summary>Returns one canned XML body for every request, so tests never hit the network.</summary>
file sealed class CannedXmlHandler(string xml) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });
}
