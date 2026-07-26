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

    [Fact]
    public void MapChangeStatus_Copy_IsAdded()
        => Assert.Equal("added", BoxDataProvider.MapChangeStatus("COPY"));

    [Fact]
    public void MapChangeStatus_Upload_IsUndeterminable_ReturnsNull()
    {
        // Box raises one UPLOAD event both for a brand-new file and for a new version of an
        // existing file. In this vocabulary "added" and "modified" are disjoint, so either guess
        // is outright false half the time — there is no weaker-but-still-true option. The key is
        // omitted instead, per the design's rule for a field the connector cannot determine.
        Assert.Null(BoxDataProvider.MapChangeStatus("UPLOAD"));
    }

    [Theory]
    [InlineData("ITEM_TRASH")]
    [InlineData("")]
    [InlineData(null)]
    public void MapChangeStatus_UnmappedEventType_ReturnsNull(string? eventType)
        => Assert.Null(BoxDataProvider.MapChangeStatus(eventType));

    [Fact]
    public void ToHandle_UploadEvent_LeavesMetadataNull()
    {
        // The delta path passes MapChangeStatus's result straight through, so an UPLOAD event
        // with no folder_id in hand yields no metadata at all rather than an empty dictionary.
        var sut = new BoxDataProvider(MakeBoxClient());

        var handle = sut.ToHandle("file-1", "readme.md", "sha1-abc",
            folderId: null, BoxDataProvider.MapChangeStatus("UPLOAD"));

        Assert.Null(handle.Metadata);
        MetadataContract.AssertValid(handle.Metadata, handle.Id);
    }
}
