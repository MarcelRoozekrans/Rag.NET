using Rag.NET.GraphRag;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests;

/// <summary>
/// Round-trips the provenance ids the graph records entity and relationship sources with.
/// </summary>
/// <remarks>
/// <para>
/// The format was written once, inside <c>GraphEntityExtractionBehavior</c>, and never read back —
/// nothing resolved a provenance id to a chunk until local search needed the source chunks its
/// selected entities came from. A formatter and a parser that live apart drift, and for an identity
/// format drift means a lookup that silently finds nothing rather than one that fails.
/// </para>
/// <para>
/// The underscore case is the one worth having: document ids in this repository's own corpora
/// contain them freely.
/// </para>
/// </remarks>
public sealed class GraphChunkIdTests
{
    [Theory]
    [InlineData("doc", 0)]
    [InlineData("doc", 42)]
    [InlineData("my_doc", 3)]
    [InlineData("a_b_c_d", 17)]
    [InlineData("doc", -1)]
    [InlineData("my_doc", -12)]
    [InlineData("graphrag://communities", 0)]
    [InlineData("Ångström_2024", 7)]
    public void EveryIdRoundTrips(string documentId, int chunkIndex)
    {
        var id = GraphChunkId.Format(documentId, chunkIndex);

        Assert.True(GraphChunkId.TryParse(id, out var key), $"Failed to parse {id}.");
        Assert.Equal(documentId, key.DocumentId, StringComparer.Ordinal);
        Assert.Equal(chunkIndex, key.ChunkIndex);
    }

    /// <remarks>
    /// The split is on the <i>last</i> underscore because a chunk index never contains one. Pinned
    /// explicitly rather than left to the round-trip above, since a first-underscore split passes
    /// every single-underscore case and fails only on real document ids.
    /// </remarks>
    [Fact]
    public void TheSplitIsOnTheLastUnderscoreNotTheFirst()
    {
        Assert.True(GraphChunkId.TryParse("my_doc_3", out var key));
        Assert.Equal("my_doc", key.DocumentId, StringComparer.Ordinal);
        Assert.Equal(3, key.ChunkIndex);
    }

    /// <remarks>
    /// The formatter interpolated <c>chunk.DocumentId</c> directly before this type existed, which
    /// worked because <c>DocumentId.ToString()</c> is generated as <c>Value</c>. Asserted so that a
    /// future change to that generated form is caught here rather than by every stored graph
    /// becoming unreadable.
    /// </remarks>
    [Fact]
    public void FormattingAChunkMatchesFormattingItsPartsAndTheOriginalInterpolation()
    {
        var chunk = new TextChunk
        {
            Text = "irrelevant",
            DocumentId = new DocumentId("some_document"),
            ChunkIndex = 5,
        };

        Assert.Equal("some_document_5", GraphChunkId.Format(chunk), StringComparer.Ordinal);
        Assert.Equal(
            $"{chunk.DocumentId}_{chunk.ChunkIndex}", GraphChunkId.Format(chunk), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("_3")]
    [InlineData("doc_")]
    [InlineData("doc_notanumber")]
    public void AnUnparseableIdIsRejectedRatherThanThrowing(string? id)
    {
        Assert.False(GraphChunkId.TryParse(id, out var key));
        Assert.Equal(default, key);
    }
}
