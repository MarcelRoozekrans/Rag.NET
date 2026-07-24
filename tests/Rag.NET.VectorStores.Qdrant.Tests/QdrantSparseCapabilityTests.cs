using Rag.NET.Abstractions;
using Rag.NET.Qdrant;
using Xunit;

namespace Rag.NET.VectorStores.Qdrant.Tests;

/// <summary>
/// Docker-free coverage of the sparse capability split and the deterministic point-id
/// contract (constructing the stores never contacts a server — the gRPC client connects
/// lazily).
/// </summary>
public class QdrantSparseCapabilityTests
{
    [Fact]
    public void DenseStore_DoesNotAdvertiseSparseCapability()
    {
        // The `is ISparseSearchable` probe in the ingestion/retrieval pipelines must be
        // honest: a dense-only Qdrant store must never trigger SPLADE encoding work.
        using var store = new QdrantVectorStore("localhost", 6334, "dense-only", 3);

        Assert.False(store is ISparseSearchable);
    }

    [Fact]
    public void SparseStore_AdvertisesSparseCapability()
    {
        using var store = new QdrantSparseVectorStore("localhost", 6334, "sparse", 3);

        Assert.True(store is ISparseSearchable);
        Assert.True(store is QdrantVectorStore); // still substitutable for the dense store
    }

    [Fact]
    public void DeterministicPointId_PinnedValue_NeverChanges()
    {
        // PERSISTENT STORAGE CONTRACT: existing Qdrant collections hold points addressed by
        // these ids. If this test fails, the id algorithm changed and previously stored
        // points become unreachable for sparse updates/idempotent upserts — do NOT update
        // the expected value; fix the algorithm instead.
        var id = QdrantSparseVectorStore.DeterministicPointId("doc-1", 0);

        Assert.Equal(Guid.Parse("8dc4cc1b-8269-8afe-b6fb-9e4d0efa24cf"), id);
    }

    [Fact]
    public void DeterministicPointId_StableAndDistinct()
    {
        var id = QdrantSparseVectorStore.DeterministicPointId("doc-1", 0);

        Assert.Equal(id, QdrantSparseVectorStore.DeterministicPointId("doc-1", 0));
        Assert.NotEqual(id, QdrantSparseVectorStore.DeterministicPointId("doc-1", 1));
        Assert.NotEqual(id, QdrantSparseVectorStore.DeterministicPointId("doc-2", 0));
    }

    [Fact]
    public void DeterministicPointId_HasValidVersionAndVariantBits()
    {
        var id = QdrantSparseVectorStore.DeterministicPointId("any-doc", 7);

        Span<byte> bytes = stackalloc byte[16];
        Assert.True(id.TryWriteBytes(bytes));
        Assert.Equal(0x80, bytes[7] & 0xF0); // version 8 (custom, RFC 9562)
        Assert.Equal(0x80, bytes[8] & 0xC0); // RFC 4122 variant
    }
}
