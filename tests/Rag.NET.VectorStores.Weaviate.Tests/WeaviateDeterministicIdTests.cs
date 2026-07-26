using Xunit;

namespace Rag.NET.Weaviate.Tests;

/// <summary>Docker-free coverage of the deterministic object-id contract.</summary>
public class WeaviateDeterministicIdTests
{
    [Fact]
    public void DeterministicObjectId_PinnedValue_NeverChanges()
    {
        // PERSISTENT STORAGE CONTRACT: existing Weaviate classes hold objects addressed by
        // these ids (byte-identical to the Qdrant sparse point-id derivation — both delegate
        // to the shared DeterministicChunkId). If this test fails, the id algorithm changed
        // and previously stored objects stop being replaced on re-ingestion — do NOT update
        // the expected value; fix the algorithm instead.
        var id = WeaviateVectorStore.DeterministicObjectId("doc-1", 0);

        Assert.Equal(Guid.Parse("8dc4cc1b-8269-8afe-b6fb-9e4d0efa24cf"), id);
    }

    [Fact]
    public void DeterministicObjectId_StableAndDistinct()
    {
        var id = WeaviateVectorStore.DeterministicObjectId("doc-1", 0);

        Assert.Equal(id, WeaviateVectorStore.DeterministicObjectId("doc-1", 0));
        Assert.NotEqual(id, WeaviateVectorStore.DeterministicObjectId("doc-1", 1));
        Assert.NotEqual(id, WeaviateVectorStore.DeterministicObjectId("doc-2", 0));
    }
}
