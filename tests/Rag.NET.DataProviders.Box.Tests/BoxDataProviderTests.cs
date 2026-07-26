using Box.V2;
using Box.V2.Config;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Box;
using Rag.NET.DataProviders.Testing;
using Xunit;

namespace Rag.NET.DataProviders.Box.Tests;

public sealed class BoxDataProviderTests
{
    private static BoxClient MakeBoxClient()
    {
        var config = new BoxConfig("clientId", "clientSecret", new Uri("https://localhost"));
        return new BoxClient(config);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BoxDataProvider(null!));
    }

    [Fact]
    public void Constructor_WithOptions_Succeeds()
    {
        var client = MakeBoxClient();
        var opts = new BoxOptions { RootFolderId = "123", Extensions = [".pdf"] };

        var sut = new BoxDataProvider(client, opts);

        Assert.NotNull(sut);
    }

    [Fact]
    public void AddBoxDataProvider_BoxClient_RegistersIFileContentProvider()
    {
        var services = new ServiceCollection();
        var client = MakeBoxClient();

        services.AddBoxDataProvider(client);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IFileContentProvider>();
        Assert.IsType<BoxDataProvider>(provider);
    }

    [Fact]
    public void AddBoxDataProvider_NullClient_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddBoxDataProvider((BoxClient)null!));
    }

    // -----------------------------------------------------------------------
    // Metadata — the exact keys and values this connector emits
    //
    // BoxClient is a concrete type with no injectable transport, so GetFilesAsync cannot be
    // driven without hitting the network. These tests call the internal ToHandle that both
    // enumeration paths funnel through, which is where the metadata is actually built.
    // -----------------------------------------------------------------------

    [Fact]
    public void ToHandle_FullRun_EmitsFolderIdOnly()
    {
        var sut = new BoxDataProvider(MakeBoxClient());

        var handle = sut.ToHandle(
            "file-1", "readme.md", "sha1-abc", folderId: "folder-42", changeStatus: null);

        Assert.NotNull(handle.Metadata);
        Assert.Equal("folder-42", handle.Metadata["folder_id"]);
        // A full traversal has no notion of change — the key must be absent, not empty.
        Assert.False(handle.Metadata.ContainsKey("change_status"));
        _ = Assert.Single(handle.Metadata);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }

    [Fact]
    public void ToHandle_DeltaRun_EmitsChangeStatusOnly()
    {
        var sut = new BoxDataProvider(MakeBoxClient());

        var handle = sut.ToHandle(
            "file-1", "readme.md", "sha1-abc", folderId: null, changeStatus: "added");

        Assert.NotNull(handle.Metadata);
        Assert.Equal("added", handle.Metadata["change_status"]);
        // The Events feed does not report a containing folder and the fields selection does not
        // request path_collection, so folder_id is omitted rather than written empty.
        Assert.False(handle.Metadata.ContainsKey("folder_id"));
        _ = Assert.Single(handle.Metadata);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }

    [Fact]
    public void ToHandle_NothingInHand_LeavesMetadataNull()
    {
        // Nothing to add means null, not an empty dictionary — the pipeline branches on
        // "is not null", so an empty dictionary would be a second representation of the same thing.
        var sut = new BoxDataProvider(MakeBoxClient());

        var handle = sut.ToHandle(
            "file-1", "readme.md", "sha1-abc", folderId: null, changeStatus: null);

        Assert.Null(handle.Metadata);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }

    [Theory]
    // Box raises one UPLOAD event for a new file and for a new version of an existing file, and
    // the payload cannot distinguish them; "modified" is the weaker claim and never actively wrong.
    [InlineData("UPLOAD", "modified")]
    [InlineData("COPY",   "added")]
    public void MapChangeStatus_NormalisesBoxEventTypes(string eventType, string expected)
        => Assert.Equal(expected, BoxDataProvider.MapChangeStatus(eventType));

    [Theory]
    [InlineData("ITEM_TRASH")]
    [InlineData("")]
    [InlineData(null)]
    public void MapChangeStatus_UnmappedEventType_ReturnsNull(string? eventType)
        => Assert.Null(BoxDataProvider.MapChangeStatus(eventType));
}
