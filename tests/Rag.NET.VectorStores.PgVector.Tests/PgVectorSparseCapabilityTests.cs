using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.PgVector.Tests;

/// <summary>
/// Docker-free coverage of the sparse capability split: constructing a store only builds an
/// <c>NpgsqlDataSource</c>, which connects lazily.
/// </summary>
public class PgVectorSparseCapabilityTests
{
    private const string ConnectionString = "Host=localhost;Database=test";

    [Fact]
    public void DenseStore_IsNotSparseSearchable()
    {
        // The `is ISparseSearchable` probe in the ingestion/retrieval pipelines must be honest:
        // a dense-only PgVector store must never trigger SPLADE encoding work, or
        // SparseEmbeddingBehavior computes vectors nothing can store.
        using var store = new PgVectorStore(ConnectionString, vectorDimensions: 3);

        Assert.False(store is ISparseSearchable);
    }

    [Fact]
    public void SparseStore_IsSparseSearchable()
    {
        using var store = new PgVectorSparseVectorStore(ConnectionString, vectorDimensions: 3);

        Assert.True(store is ISparseSearchable);
    }

    [Fact]
    public void SparseStore_IsStillSubstitutableForTheDenseStore()
    {
        using var store = new PgVectorSparseVectorStore(ConnectionString, vectorDimensions: 3);

        Assert.True(store is PgVectorStore);
    }

    [Fact]
    public void UsePgVector_WithSparseVectorsEnabled_RegistersTheSparseStore()
    {
        var provider = BuildProvider(enableSparseVectors: true);

        var store = provider.GetRequiredService<IVectorStore>();

        Assert.IsType<PgVectorSparseVectorStore>(store);
        Assert.Same(store, provider.GetRequiredService<ICollectionManageable>());
    }

    [Fact]
    public void UsePgVector_ByDefault_RegistersTheDenseStore()
    {
        var provider = BuildProvider(enableSparseVectors: false);

        var store = provider.GetRequiredService<IVectorStore>();

        Assert.IsType<PgVectorStore>(store);
        Assert.False(store is ISparseSearchable);
    }

    [Theory]
    [InlineData("0.7.0", true)]
    [InlineData("0.7.4", true)]
    [InlineData("0.8.2", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.6.2", false)]
    [InlineData("0.5.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not-a-version", false)]
    [InlineData("0", false)]
    public void SupportsSparseVectors_GatesOnPgvector07(string? extensionVersion, bool expected)
    {
        // sparsevec arrived in pgvector 0.7.0. The end-to-end failure path cannot be exercised
        // against the pinned pgvector/pgvector:pg17 image (0.8.2), so the comparison is verified
        // here and the InitializeAsync wiring is verified by construction.
        Assert.Equal(expected, PgVectorSparseVectorStore.SupportsSparseVectors(extensionVersion));
    }

    [Fact]
    public async Task StoreSparseAsync_AboveTheHnswNonZeroCap_ThrowsNamingTopTerms()
    {
        // The whole batch is validated before a connection is opened, so this needs no server.
        using var store = new PgVectorSparseVectorStore(ConnectionString, vectorDimensions: 3);

        var indices = new int[1001];
        var values = new float[1001];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
            values[i] = 1.0f;
        }

        var chunk = new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("doc-wide"), ChunkIndex = 3 },
            Embedding = new float[] { 1.0f, 0.0f, 0.0f },
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreSparseAsync(
                [(chunk, new SparseVector { Indices = indices, Values = values })],
                TestContext.Current.CancellationToken));

        Assert.Contains("1001 non-zero terms", ex.Message, StringComparison.Ordinal);
        Assert.Contains("more than 1000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OnnxSpladeOptions.TopTerms", ex.Message, StringComparison.Ordinal);
        Assert.Contains("idx_rag_chunks_sparse", ex.Message, StringComparison.Ordinal);
        Assert.Contains("('doc-wide', 3)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreSparseAsync_AllVectorsEmpty_DoesNotTouchTheDatabase()
    {
        // The contract skips empty vectors; skipping them all must not even open a connection
        // (this connection string points at nothing).
        using var store = new PgVectorSparseVectorStore(ConnectionString, vectorDimensions: 3);

        var chunk = new EmbeddedChunk
        {
            Chunk = new TextChunk { Text = "t", DocumentId = new DocumentId("doc-empty"), ChunkIndex = 0 },
            Embedding = new float[] { 1.0f, 0.0f, 0.0f },
        };

        await store.StoreSparseAsync([(chunk, SparseVector.Empty)], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchSparseAsync_EmptyQuery_ReturnsEmptyWithoutARoundTrip()
    {
        using var store = new PgVectorSparseVectorStore(ConnectionString, vectorDimensions: 3);

        var results = await store.SearchSparseAsync(
            SparseVector.Empty, new SearchOptions { TopK = 10 }, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    private static IServiceProvider BuildProvider(bool enableSparseVectors) =>
        new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector(
                ConnectionString,
                vectorDimensions: 3,
                enableSparseVectors: enableSparseVectors))
            .BuildServiceProvider();
}
