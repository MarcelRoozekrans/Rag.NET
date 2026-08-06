using Xunit;

namespace Rag.NET.Tests.Serialization;

/// <summary>
/// Pins the write-time consumer the ChunkIndex collision defect corrupted:
/// <see cref="DeterministicChunkId.Derive"/> keys the deterministic Qdrant/Weaviate point id
/// on <c>(DocumentId, ChunkIndex)</c>. Before the ParseBehavior fix, two chunks from different
/// sections of the same document could share a ChunkIndex and therefore derive the SAME guid —
/// the second chunk written would silently overwrite the first at the vector store.
/// </summary>
public class DeterministicChunkIdTests
{
    [Fact]
    public void Derive_DifferentChunkIndicesSameDocument_ProducesDistinctGuids()
    {
        // Simulates two chunks from different sections of the same document that, before the
        // ParseBehavior fix, would have collided at ChunkIndex 0 (first chunk of each section).
        var first = DeterministicChunkId.Derive("doc-1", 0);
        var second = DeterministicChunkId.Derive("doc-1", 1);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Derive_SameDocumentIdAndChunkIndex_IsDeterministic()
    {
        var first = DeterministicChunkId.Derive("doc-1", 3);
        var second = DeterministicChunkId.Derive("doc-1", 3);

        Assert.Equal(first, second);
    }
}
