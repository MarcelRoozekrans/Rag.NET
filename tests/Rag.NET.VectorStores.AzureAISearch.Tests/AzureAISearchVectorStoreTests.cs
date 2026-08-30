using System.Diagnostics;
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

[Collection("AzureAISearch")]
public class AzureAISearchVectorStoreTests : IAsyncLifetime
{
    private readonly IContainer _simulator = new ContainerBuilder("ghcr.io/ellerbach/azure-ai-search-simulator:latest")
        .WithPortBinding(8080, true)
        .WithPortBinding(8443, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on:"))
        .Build();

    private AzureAISearchVectorStore _sut = null!;
    private readonly string _indexName = $"ragnet-test-{Guid.CreateVersion7():N}"[..24];

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

        var options = new AzureSearchClientOptions
        {
            Transport = new HttpClientTransport(httpHandler),
        };

        _sut = new AzureAISearchVectorStore(
            new Uri($"https://localhost:{httpsPort}"),
            _indexName,
            new AzureKeyCredential("admin-key-12345"),
            vectorDimensions: 3,
            clientOptions: options);

        await _sut.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _simulator.DisposeAsync();
    }

    [Fact]
    public async Task DeleteCollection_Missing_IsNoOp()
    {
        // The ICollectionManageable contract makes delete-of-missing a no-op; the Azure SDK
        // surfaces the service's 404 as a RequestFailedException, so the store absorbs it.
        ICollectionManageable manageable = _sut;

        await manageable.DeleteCollectionAsync(
            $"never-created-{Guid.CreateVersion7():N}"[..24],
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StoreAndSearch_ReturnsRelevantResults()
    {
        var docId = $"ais-{Guid.CreateVersion7():N}";
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "cats are great", DocumentId = new DocumentId(docId), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "dogs are great", DocumentId = new DocumentId(docId), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId, 2, TestContext.Current.CancellationToken);

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 1 },
                TestContext.Current.CancellationToken);

            Assert.Single(results);
            Assert.Equal("cats are great", results[0].Chunk.Text);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task HybridSearch_FusesKeywordAndVectorArms()
    {
        // Three chunks arranged so only genuine two-arm fusion returns the right pair:
        // the query vector is nearest "alpha", the text query matches only "zebra", and
        // TopK = 2 — a dense-only search returns alpha + one orthogonal filler, a
        // keyword-only search returns just zebra. Score magnitudes are deliberately not
        // asserted: the local simulator's fusion arithmetic is not the real service's
        // (the service documents hybrid scores as RRF values, ~1/60 per arm).
        var docId = $"ais-{Guid.CreateVersion7():N}";
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "alpha document", DocumentId = new DocumentId(docId), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "middle document", DocumentId = new DocumentId(docId), ChunkIndex = 1 },
                Embedding = new float[] { 0.0f, 1.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk { Text = "zebra document", DocumentId = new DocumentId(docId), ChunkIndex = 2 },
                Embedding = new float[] { 0.0f, 0.0f, 1.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId, 3, TestContext.Current.CancellationToken);

            var results = await _sut.HybridSearchAsync(
                "zebra",
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 2 },
                TestContext.Current.CancellationToken);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => string.Equals(r.Chunk.Text, "alpha document", StringComparison.Ordinal));
            Assert.Contains(results, r => string.Equals(r.Chunk.Text, "zebra document", StringComparison.Ordinal));
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteByDocumentId_RemovesAllChunksForDocument()
    {
        var docId = $"ais-{Guid.CreateVersion7():N}";
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk { Text = "text1", DocumentId = new DocumentId(docId), ChunkIndex = 0 },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId, 1, TestContext.Current.CancellationToken);

            await _sut.DeleteByDocumentIdAsync(docId, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId, 0, TestContext.Current.CancellationToken);

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 10 },
                TestContext.Current.CancellationToken);

            Assert.Empty(results);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task StoreAndSearch_TypedMetadata_KindsSurviveRoundTrip()
    {
        // A number reading back as the string "3" is the flattening bug the typed metadata
        // design removes (#91) — so the assertion is on Kind, not on textual form. The values
        // travel through the metadata_entries Collection(Edm.ComplexType), one typed slot per
        // kind, not through the legacy metadata JSON blob.
        var reviewedAt = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var docId = $"ais-{Guid.CreateVersion7():N}";
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "typed metadata chunk", DocumentId = new DocumentId(docId), ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["page"] = 3,
                        ["rating"] = 4.5,
                        ["published"] = true,
                        ["reviewed_at"] = reviewedAt,
                        ["source"] = "unit",
                    },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId, 1, TestContext.Current.CancellationToken);

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions { TopK = 1 },
                TestContext.Current.CancellationToken);

            var metadata = Assert.Single(results).Chunk.Metadata;
            Assert.Equal(MetadataValueKind.Number, metadata["page"].Kind);
            Assert.Equal(3d, metadata["page"].NumberValue);
            Assert.Equal(4.5, metadata["rating"].NumberValue);
            Assert.Equal(MetadataValueKind.Boolean, metadata["published"].Kind);
            Assert.True(metadata["published"].BooleanValue);
            Assert.Equal(MetadataValueKind.DateTimeOffset, metadata["reviewed_at"].Kind);
            Assert.Equal(reviewedAt, metadata["reviewed_at"].DateTimeOffsetValue);
            Assert.Equal(MetadataValueKind.String, metadata["source"].Kind);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId, CancellationToken.None);
        }
    }

    [Fact(Skip = "azure-ai-search-simulator does not implement OData filter expressions")]
    public async Task Search_WithMetadataFilter_FiltersResults()
    {
        var docId1 = $"ais-{Guid.CreateVersion7():N}";
        var docId2 = $"ais-{Guid.CreateVersion7():N}";
        var chunks = new List<EmbeddedChunk>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "engineering doc", DocumentId = new DocumentId(docId1), ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                Embedding = new float[] { 1.0f, 0.0f, 0.0f },
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "marketing doc", DocumentId = new DocumentId(docId2), ChunkIndex = 0,
                    Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "marketing" },
                },
                Embedding = new float[] { 0.9f, 0.1f, 0.0f },
            },
        };

        try
        {
            await _sut.StoreAsync(chunks, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId1, 1, TestContext.Current.CancellationToken);
            await WaitForVisibleChunksAsync(docId2, 1, TestContext.Current.CancellationToken);

            var results = await _sut.SearchAsync(
                new float[] { 1.0f, 0.0f, 0.0f },
                new SearchOptions
                {
                    TopK = 10,
                    MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal) { ["department"] = "engineering" },
                },
                TestContext.Current.CancellationToken);

            Assert.Single(results);
            Assert.Equal("engineering doc", results[0].Chunk.Text);
        }
        finally
        {
            await _sut.DeleteByDocumentIdAsync(docId1, CancellationToken.None);
            await _sut.DeleteByDocumentIdAsync(docId2, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CollectionManageable_CreateAndDeleteCollection()
    {
        ICollectionManageable manageable = (ICollectionManageable)_sut;
        var tempIndex = $"temp-{Guid.CreateVersion7():N}"[..24];

        await manageable.CreateCollectionAsync(tempIndex, 3, TestContext.Current.CancellationToken);
        Assert.True(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));

        await manageable.DeleteCollectionAsync(tempIndex, TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            $"index '{tempIndex}' no longer exists",
            async () => !await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.False(await manageable.CollectionExistsAsync(tempIndex, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EscapeODataString_ValueWithSingleQuote_DoublesSingleQuotes()
    {
        Assert.Equal("it''s here", AzureAISearchVectorStore.EscapeODataString("it's here"));
    }

    [Fact]
    public void EscapeODataString_ValueWithNoSpecialChars_ReturnsUnchanged()
    {
        Assert.Equal("normal", AzureAISearchVectorStore.EscapeODataString("normal"));
    }

    [Fact]
    public void EscapeODataString_ValueWithMultipleSingleQuotes_DoublesAllOfThem()
    {
        Assert.Equal("it''s a ''test''", AzureAISearchVectorStore.EscapeODataString("it's a 'test'"));
    }

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Polls until <paramref name="what"/> holds, instead of sleeping a fixed guess.</summary>
    /// <remarks>
    /// Every write in this class used to be followed by <c>await Task.Delay(2s)</c>. That is the
    /// same mistake the store itself made — its <c>StoreAsync</c> slept a second on every call —
    /// and a fixed sleep is wrong in both directions: too short and the test flakes, too long and
    /// every test pays the worst case whether or not it needs to. A poll returns as soon as the
    /// service is actually ready, and on expiry fails naming the condition that never came true
    /// rather than leaving an assertion to fail for a reason that reads like a product bug.
    /// </remarks>
    private static async Task WaitUntilAsync(
        string what, Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(start) >= SettleTimeout)
            {
                Assert.Fail($"'{what}' never became true within {SettleTimeout.TotalSeconds:0}s.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until exactly <paramref name="expected"/> chunks of the document are searchable.
    /// </summary>
    /// <remarks>
    /// Counts by document id rather than trusting the total: the index is shared by every test in
    /// this class, so another test's leftovers would otherwise satisfy the wait.
    /// </remarks>
    private Task WaitForVisibleChunksAsync(
        string documentId, int expected, CancellationToken cancellationToken) =>
        WaitUntilAsync(
            $"{expected} chunk(s) visible for document '{documentId}'",
            async () =>
            {
                var results = await _sut.SearchAsync(
                    new float[] { 1.0f, 0.0f, 0.0f },
                    new SearchOptions { TopK = 100 },
                    cancellationToken);
                return results.Count(r => r.Chunk.DocumentId == new DocumentId(documentId)) == expected;
            },
            cancellationToken);
}
