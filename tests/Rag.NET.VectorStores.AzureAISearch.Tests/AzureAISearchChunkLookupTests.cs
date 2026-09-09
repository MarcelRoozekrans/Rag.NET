using Azure;
using Azure.Core.Pipeline;
using AzureSearchClientOptions = Azure.Search.Documents.SearchClientOptions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

/// <summary>
/// <see cref="AzureAISearchVectorStore"/>'s keyed lookup, and the derived document key that made it
/// possible (#318, #517).
/// </summary>
/// <remarks>
/// <para>
/// <b>This backend could not answer a keyed read at all until the key changed.</b> The document key
/// was <c>Guid.NewGuid()</c>, so identity lived only in fields — and a lookup would have had to
/// filter on <c>chunk_index</c>, which is declared without <c>IsFilterable</c> and would be
/// rejected by the service. Deriving the key turns the lookup into a direct <c>GetDocument</c>,
/// which needs no filter and is therefore verifiable against the local simulator.
/// </para>
/// <para>
/// <b>The same change fixes re-ingest.</b> Azure's <c>Upload</c> replaces the document with the
/// given key, so a random key made every write an insert: storing one chunk twice left two
/// searchable, measured. These tests pin the upsert as well as the lookup, because the two are the
/// same defect seen from opposite ends.
/// </para>
/// </remarks>
[Collection("AzureAISearch")]
public class AzureAISearchChunkLookupTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithPortBinding(8443, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
        .Build();

    private AzureAISearchVectorStore _sut = null!;
    private readonly string _indexName = $"ragnet-look-{Guid.CreateVersion7():N}"[..24];

    public async ValueTask InitializeAsync()
    {
        await _simulator.StartAsync(TestContext.Current.CancellationToken);
        var httpsPort = _simulator.GetMappedPublicPort(8443);

        var httpHandler = new HttpClientHandler
        {
#pragma warning disable MA0039 // Do not write your own certificate validation method — intentional for local test simulator
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
#pragma warning restore MA0039
        };

        _sut = new AzureAISearchVectorStore(
            new Uri($"https://localhost:{httpsPort}"),
            _indexName,
            new AzureKeyCredential("admin-key-12345"),
            vectorDimensions: 3,
            clientOptions: new AzureSearchClientOptions { Transport = new HttpClientTransport(httpHandler) });

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _sut.Dispose();
        await _simulator.DisposeAsync();
    }

    private static EmbeddedChunk Chunk(
        string documentId,
        int chunkIndex,
        string text,
        Dictionary<string, MetadataValue>? metadata = null) => new()
    {
        Chunk = new TextChunk
        {
            Text = text,
            DocumentId = new DocumentId(documentId),
            ChunkIndex = chunkIndex,
            Metadata = metadata ?? new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
        },
        Embedding = new float[] { 1.0f, 0.0f, 0.0f },
    };

    private async Task StoreAndSettleAsync(params EmbeddedChunk[] chunks)
    {
        await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheStoreReportsThatItSupportsLookup()
    {
        var lookup = Assert.IsAssignableFrom<IChunkLookup>(_sut);
        Assert.True(lookup.SupportsChunkLookup);

        await Task.CompletedTask;
    }

    /// <summary>
    /// <b>The defect #517 recorded.</b> Storing the same chunk twice must leave one document, not
    /// two. With a random key it left two — measured before the fix — and nothing warned.
    /// </summary>
    [Fact]
    public async Task StoringTheSameChunkTwiceReplacesItRatherThanDuplicating()
    {
        var docId = $"dup-{Guid.CreateVersion7():N}";
        var ct = TestContext.Current.CancellationToken;

        await StoreAndSettleAsync(Chunk(docId, 0, "first write"));
        await StoreAndSettleAsync(Chunk(docId, 0, "second write"));

        var results = await _sut.SearchAsync(
            new float[] { 1.0f, 0.0f, 0.0f }, new SearchOptions { TopK = 100 }, ct);

        var mine = results.Where(r => r.Chunk.DocumentId == new DocumentId(docId)).ToList();
        var only = Assert.Single(mine);
        Assert.Equal("second write", only.Chunk.Text);
    }

    /// <summary>
    /// Chunks come back by identity, and the pairs are matched as pairs — a key naming a document
    /// that exists at an index that does not must not return another document's chunk at that
    /// index. Here that follows from the key composition.
    /// </summary>
    [Fact]
    public async Task ChunksComeBackByKey_AndThePairsAreMatchedAsPairs()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = $"pa-{Guid.CreateVersion7():N}";
        var b = $"pb-{Guid.CreateVersion7():N}";

        await StoreAndSettleAsync(
            Chunk(a, 0, "a0"), Chunk(a, 1, "a1"), Chunk(b, 0, "b0"), Chunk(b, 1, "b1"));

        var found = await _sut.GetChunksAsync([new ChunkKey(a, 1), new ChunkKey(b, 0)], ct);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, a, StringComparison.Ordinal) && c.ChunkIndex == 1);
        Assert.Contains(found, c =>
            string.Equals(c.DocumentId.Value, b, StringComparison.Ordinal) && c.ChunkIndex == 0);
    }

    /// <summary>
    /// <b>The trap #318 names, and the only test that has caught an unsigned implementation on
    /// every backend.</b> <c>GraphEntityExtractionBehavior</c> assigns <c>-(i + 1)</c> to synthetic
    /// entity and relationship chunks, so negative indices are exactly the documents GraphRAG asks
    /// for.
    /// </summary>
    [Fact]
    public async Task NegativeChunkIndicesAreKeysLikeAnyOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"neg-{Guid.CreateVersion7():N}";

        await StoreAndSettleAsync(
            Chunk(docId, -1, "entity one"),
            Chunk(docId, -2, "relationship one"),
            Chunk(docId, 0, "ordinary"));

        var found = await _sut.GetChunksAsync(
            [new ChunkKey(docId, -1), new ChunkKey(docId, -2)], ct);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, c => string.Equals(c.Text, "entity one", StringComparison.Ordinal));
        Assert.Contains(found, c => string.Equals(c.Text, "relationship one", StringComparison.Ordinal));
        Assert.DoesNotContain(found, c => string.Equals(c.Text, "ordinary", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The encoding's whole purpose.</b> Azure AI Search accepts only letters, digits, <c>_</c>,
    /// <c>-</c> and <c>=</c> in a document key, so a document id containing a slash, colon, space or
    /// non-ASCII character cannot be used raw — the service rejects the write outright. Base64Url
    /// makes every id storable, and this is the test that fails if someone "simplifies" the key back
    /// to <c>documentId + ":" + chunkIndex</c>.
    /// </summary>
    [Theory]
    [InlineData("doc/with/slashes")]
    [InlineData("doc:with:colons")]
    [InlineData("doc with spaces")]
    [InlineData("документ")]
    [InlineData("doc#hash&amp")]
    public async Task DocumentIdsAzureWouldRejectAsAKeyStillRoundTrip(string awkward)
    {
        var ct = TestContext.Current.CancellationToken;
        var unique = $"{awkward}-{Guid.CreateVersion7():N}";

        await StoreAndSettleAsync(Chunk(unique, 0, "awkward id"));

        var found = await _sut.GetChunksAsync([new ChunkKey(unique, 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("awkward id", only.Text);
        Assert.Equal(unique, only.DocumentId.Value);
    }

    /// <summary>
    /// Distinct chunks never share a key. A separator scheme that concatenated without encoding
    /// could map two different pairs onto one key — silently merging two chunks into one document,
    /// which no test that stores a single chunk can see.
    /// </summary>
    [Fact]
    public void DistinctPairsProduceDistinctKeys()
    {
        // "a\n1" + 2 and "a" + "1\n2" would collide under naive concatenation with a newline
        // separator; the index is an int, so only the first is a real pair — but the encoding must
        // still separate ids that themselves contain the separator.
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            AzureAISearchVectorStore.DocumentKey("a", 12),
            AzureAISearchVectorStore.DocumentKey("a1", 2),
            AzureAISearchVectorStore.DocumentKey("a\n1", 2),
            AzureAISearchVectorStore.DocumentKey("a", -12),
            AzureAISearchVectorStore.DocumentKey("a-1", 2),
        };

        Assert.Equal(5, keys.Count);
    }

    /// <summary>Every key uses only the characters Azure AI Search permits.</summary>
    [Theory]
    [InlineData("doc/with/slashes", 0)]
    [InlineData("документ", -3)]
    [InlineData("doc with spaces", 42)]
    public void KeysUseOnlyThePermittedCharacterSet(string documentId, int chunkIndex)
    {
        var key = AzureAISearchVectorStore.DocumentKey(documentId, chunkIndex);

        Assert.NotEmpty(key);
        Assert.All(key, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '=',
            $"key '{key}' contains '{c}', which Azure AI Search does not accept in a document key"));
    }

    /// <summary>A key with no stored chunk is absent, not an error — a 404 is ordinary here.</summary>
    [Fact]
    public async Task AKeyWithNoStoredChunkIsAbsentRatherThanAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"abs-{Guid.CreateVersion7():N}";

        await StoreAndSettleAsync(Chunk(docId, 0, "a0"));

        var found = await _sut.GetChunksAsync(
            [new ChunkKey(docId, 0), new ChunkKey(docId, 99), new ChunkKey("no-such-doc", 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal("a0", only.Text);
    }

    /// <summary>No keys means no round trip and no documents.</summary>
    [Fact]
    public async Task NoKeysReturnsNothing()
    {
        var found = await _sut.GetChunksAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    /// <summary>Metadata survives the keyed read.</summary>
    [Fact]
    public async Task MetadataComesBackWithTheChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var docId = $"meta-{Guid.CreateVersion7():N}";

        await StoreAndSettleAsync(
            Chunk(docId, 0, "with metadata",
                new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["source"] = "unit-test" }));

        var found = await _sut.GetChunksAsync([new ChunkKey(docId, 0)], ct);

        var only = Assert.Single(found);
        Assert.Equal<MetadataValue>("unit-test", only.Metadata["source"]);
    }
}
