using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
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

    private static IServiceProvider BuildProvider(bool enableSparseVectors) =>
        new ServiceCollection()
            .AddRagNet(rag => rag.UsePgVector(
                ConnectionString,
                vectorDimensions: 3,
                enableSparseVectors: enableSparseVectors))
            .BuildServiceProvider();
}
