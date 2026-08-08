using Box.Sdk.Gen;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DataProviders;
using Rag.NET.DataProviders.Box;
using Rag.NET.DataProviders.Testing;
using Xunit;
using BoxFile = Box.Sdk.Gen.Schemas.File;

namespace Rag.NET.DataProviders.Box.Tests;

public sealed class BoxDataProviderTests
{
    private static BoxClient MakeBoxClient() => new(new BoxDeveloperTokenAuth("fake-token"));

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
    // ToHandle/MapChangeStatus are pinned directly (as before the migration) so the emitted
    // metadata keys stay exact regardless of what the enumeration loops around them do. The
    // enumeration loops themselves are now covered separately below, through a fake
    // INetworkClient — see the "Enumeration" region.
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

    /// <summary>
    /// Phase 4.10 Task 6: the full-run field selection was widened with <c>created_at</c>/
    /// <c>modified_at</c>, and <c>ToHandle</c> now forwards the SDK's <c>DateTimeOffset?</c>
    /// <c>File.CreatedAt</c>/<c>ModifiedAt</c> onto the typed
    /// <see cref="FileHandle.CreatedAt"/>/<see cref="FileHandle.UpdatedAt"/> channel (as
    /// <c>DateTime?</c>, via <c>UtcDateTime</c>).
    /// </summary>
    [Fact]
    public void ToHandle_SourceWithTimestamps_PopulatesTypedCreatedAndUpdatedAt()
    {
        // Box.Sdk.Gen's File exposes public setters (unlike the 3.x SDK's BoxItem, which forced
        // a Newtonsoft round-trip to populate CreatedAt/ModifiedAt), so it is built directly.
        var sut = new BoxDataProvider(MakeBoxClient());
        var source = new BoxFile("file-1")
        {
            Name = "readme.md",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 2, 1, 10, 30, 0, TimeSpan.Zero),
        };

        var handle = sut.ToHandle(
            "file-1", "readme.md", "sha1-abc", folderId: "folder-42", changeStatus: null,
            source: source);

        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), handle.CreatedAt);
        Assert.Equal(new DateTime(2026, 2, 1, 10, 30, 0, DateTimeKind.Utc), handle.UpdatedAt);
    }

    /// <summary>
    /// A missing <c>source</c> (or a source with unset timestamps) must leave
    /// <see cref="FileHandle.CreatedAt"/>/<see cref="FileHandle.UpdatedAt"/> null rather than
    /// throwing or defaulting to some fabricated value.
    /// </summary>
    [Fact]
    public void ToHandle_NoSource_LeavesTypedTimestampsNull()
    {
        var sut = new BoxDataProvider(MakeBoxClient());

        var handle = sut.ToHandle(
            "file-1", "readme.md", "sha1-abc", folderId: "folder-42", changeStatus: null);

        Assert.Null(handle.CreatedAt);
        Assert.Null(handle.UpdatedAt);
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

    // -----------------------------------------------------------------------
    // Enumeration — GetFullHandlesAsync/GetDeltaHandlesAsync via a fake INetworkClient
    //
    // Box.Sdk.Gen.BoxClient accepts a NetworkSession built with WithNetworkClient(INetworkClient),
    // a one-method FetchAsync(FetchOptions) -> Task<FetchResponse> seam. That makes the recursive
    // folder traversal, its pagination, the delta stream's chunking/cursor advance, and the event
    // filter reachable end-to-end from GetFilesAsync — none of this was drivable under the 3.x
    // SDK's concrete BoxClient (see the note atop the metadata region above).
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetFilesAsync_FullRun_RecursesFoldersAndPaginates()
    {
        var client = FakeBoxNetworkClient.MakeClient(FullRunResponses);
        var sut = new BoxDataProvider(client);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results);
        Assert.Equal(2, results.Count);
        var f1 = results.Single(r => string.Equals(r.Value.Id, "f1", StringComparison.Ordinal)).Value;
        var f2 = results.Single(r => string.Equals(r.Value.Id, "f2", StringComparison.Ordinal)).Value;
        Assert.Equal("0", f1.Metadata!["folder_id"]);
        Assert.Equal("d1", f2.Metadata!["folder_id"]);
        Assert.Equal(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), f1.CreatedAt);
    }

    [Fact]
    public async Task GetFilesAsync_FullRun_DownloadsForwardToDownloadsManager()
    {
        var client = FakeBoxNetworkClient.MakeClient(FullRunResponses);
        var sut = new BoxDataProvider(client);
        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var f1 = results.Single(r => string.Equals(r.Value.Id, "f1", StringComparison.Ordinal)).Value;

        await using var stream = await f1.OpenContentAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        Assert.Equal("content-of-f1", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetFilesAsync_DeltaRun_FiltersEventsAndAdvancesCursor()
    {
        var opts = new BoxOptions { DeltaToken = "0" };
        var client = FakeBoxNetworkClient.MakeClient(DeltaRunResponses);
        var sut = new BoxDataProvider(client, opts);

        var results = await sut.GetFilesAsync(TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        MetadataContract.AssertAll(results);
        var ids = results.Select(r => (string)r.Value.Id).ToList();
        // g4 (DOWNLOAD event) and the user-sourced event are filtered out entirely.
        Assert.Equal(["g1", "g2", "g3"], ids);
        var g1 = results[0].Value;
        var g2 = results[1].Value;
        Assert.Null(g1.Metadata); // UPLOAD is undeterminable — no change_status tag
        Assert.Equal("added", g2.Metadata!["change_status"]); // COPY
    }

    private static FetchResponse FullRunResponses(FetchOptions options)
    {
        if (options.Url.EndsWith("/folders/0/items", StringComparison.Ordinal))
        {
            return string.Equals(options.Parameters!["offset"], "0", StringComparison.Ordinal)
                ? FakeBoxNetworkClient.Json("""
                    { "total_count": 2, "offset": 0, "limit": 100, "entries": [
                        { "type": "file", "id": "f1", "name": "a.txt", "sha1": "sha1-a",
                          "created_at": "2026-01-01T09:00:00Z", "modified_at": "2026-01-02T09:00:00Z" }
                      ] }
                    """)
                : FakeBoxNetworkClient.Json("""
                    { "total_count": 2, "offset": 1, "limit": 100, "entries": [
                        { "type": "folder", "id": "d1", "name": "sub" }
                      ] }
                    """);
        }
        if (options.Url.EndsWith("/folders/d1/items", StringComparison.Ordinal))
        {
            return FakeBoxNetworkClient.Json("""
                { "total_count": 1, "offset": 0, "limit": 100, "entries": [
                    { "type": "file", "id": "f2", "name": "b.txt", "sha1": "sha1-b" }
                  ] }
                """);
        }
        if (options.Url.EndsWith("/files/f1/content", StringComparison.Ordinal))
            return FakeBoxNetworkClient.Content("content-of-f1");

        throw new InvalidOperationException($"Unexpected request: {options.Url}");
    }

    private static FetchResponse DeltaRunResponses(FetchOptions options)
    {
        if (!options.Url.EndsWith("/events", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected request: {options.Url}");

        return string.Equals(options.Parameters!["stream_position"], "0", StringComparison.Ordinal)
            ? FakeBoxNetworkClient.Json("""
                { "chunk_size": 100, "next_stream_position": 500, "entries": [
                    { "event_type": "UPLOAD", "event_id": "e1",
                      "source": { "type": "file", "id": "g1", "name": "g1.txt", "sha1": "sha1-g1" } },
                    { "event_type": "COPY", "event_id": "e2",
                      "source": { "type": "file", "id": "g2", "name": "g2.txt", "sha1": "sha1-g2" } },
                    { "event_type": "DOWNLOAD", "event_id": "e3",
                      "source": { "type": "file", "id": "g4", "name": "g4.txt" } },
                    { "event_type": "UPLOAD", "event_id": "e4",
                      "source": { "type": "user", "id": "u1", "name": "Someone" } }
                  ] }
                """)
            : FakeBoxNetworkClient.Json("""
                { "chunk_size": 1, "next_stream_position": "999", "entries": [
                    { "event_type": "UPLOAD", "event_id": "e5",
                      "source": { "type": "file", "id": "g3", "name": "g3.txt", "sha1": "sha1-g3" } }
                  ] }
                """);
    }
}
