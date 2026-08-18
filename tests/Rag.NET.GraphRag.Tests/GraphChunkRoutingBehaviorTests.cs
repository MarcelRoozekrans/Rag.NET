using Microsoft.Extensions.AI;
using Rag.NET.GraphRag;
using Rag.NET.Ingestion;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// The separation itself: graph chunks reach the graph's store and nothing else does (#247).
/// </summary>
/// <remarks>
/// <para>
/// GraphRAG used to embed entities, relationships and reports into the same store as the article
/// chunks — 303,503 beside 17,648 on MultiHop-RAG. Dense retrieval treated them as peers of the
/// text, which cost <b>−0.043 nDCG@10</b> and <b>−0.21 answer accuracy</b> with depth and chunking
/// held constant.
/// </para>
/// <para>
/// These assert both halves, because either alone can pass while the separation is broken: that the
/// graph's chunks arrive in the graph's store, <i>and</i> that they are gone from the batch the
/// document store is about to receive.
/// </para>
/// </remarks>
public sealed class GraphChunkRoutingBehaviorTests
{
    [Fact]
    public async Task GraphChunksGoToTheGraphStoreAndLeaveTheBatch()
    {
        using var inner = new InMemoryVectorStore();
        var chunkStore = new GraphChunkStore(inner);
        var sut = new GraphChunkRoutingBehavior(chunkStore);

        var ctx = CreateContext();
        // Distinct chunk indices, because a store keys on (DocumentId, ChunkIndex) and three
        // synthetic chunks sharing one index overwrite each other — this fixture used -1 for all
        // three at first and two of them vanished. Production does not have that problem:
        // GraphEntityExtractionBehavior assigns -(i + 1) per chunk for exactly this reason.
        ctx.EmbeddedChunks.Add(Chunk("article text", graphType: null, index: 0));
        ctx.EmbeddedChunks.Add(Chunk("Alice is a person", graphType: "entity", index: -1));
        ctx.EmbeddedChunks.Add(Chunk("Alice knows Bob", graphType: "relationship", index: -2));
        ctx.EmbeddedChunks.Add(Chunk("A community about people", graphType: "community_report", index: -3));

        var downstream = new List<EmbeddedChunk>();
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, ct) =>
        {
            downstream.AddRange(c.EmbeddedChunks);
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 });
        });

        // What continues to the document store: the article chunk, alone.
        Assert.Single(downstream);
        Assert.Equal("article text", downstream[0].Chunk.Text, StringComparer.Ordinal);

        // And the three synthetic ones are in the graph's store instead.
        var stored = await inner.SearchAsync(
            new float[] { 0.1f, 0.2f, 0.3f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, stored.Count);
        Assert.All(stored, r => Assert.True(r.Chunk.Metadata.ContainsKey("graph_type")));
    }

    /// <remarks>
    /// A document ingested without GraphRAG producing anything must not pay for a store round trip,
    /// and must reach storage with its batch untouched.
    /// </remarks>
    [Fact]
    public async Task ADocumentWithNoGraphChunksIsPassedStraightThrough()
    {
        using var inner = new InMemoryVectorStore();
        var sut = new GraphChunkRoutingBehavior(new GraphChunkStore(inner));

        var ctx = CreateContext();
        ctx.EmbeddedChunks.Add(Chunk("article one", graphType: null, index: 0));
        ctx.EmbeddedChunks.Add(Chunk("article two", graphType: null, index: 1));

        var downstream = 0;
        await sut.HandleAsync(ctx, TestContext.Current.CancellationToken, (c, ct) =>
        {
            downstream = c.EmbeddedChunks.Count;
            return ValueTask.FromResult(new IngestionResult { DocumentId = c.Metadata.DocumentId, ChunksStored = 0 });
        });

        Assert.Equal(2, downstream);

        var stored = await inner.SearchAsync(
            new float[] { 0.1f, 0.2f, 0.3f },
            new SearchOptions { TopK = 10 },
            TestContext.Current.CancellationToken);
        Assert.Empty(stored);
    }

    private static IngestionContext CreateContext() => new()
    {
        Stream = Stream.Null,
        Metadata = new DocumentMetadata { DocumentId = new DocumentId("doc1"), FileName = "doc1.txt" },
        GetNextBm25DocId = () => 0,
    };

    private static EmbeddedChunk Chunk(string text, string? graphType, int index)
    {
        var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (graphType is not null)
        {
            metadata["graph_type"] = graphType;
        }

        return new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                Text = text,
                DocumentId = new DocumentId("doc1"),
                ChunkIndex = index,
                Metadata = metadata,
            },
            Embedding = new float[] { 0.1f, 0.2f, 0.3f },
        };
    }
}
