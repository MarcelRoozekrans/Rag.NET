using Rag.NET.Graph;
using Rag.NET.GraphRag.LocalSearch;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests.LocalSearch;

/// <summary>
/// Pins the source-chunk ordering, which spends half the context budget.
/// </summary>
/// <remarks>
/// The ordering is <c>(entity_order, −relationship_count)</c>. The leading term is the surprising
/// one and the one worth defending: <b>similarity to the query does not appear in it at all</b>.
/// Chunks are grouped by which selected entity claimed them, in selection order. A version of this
/// ordered by score would be a different retrieval system that happened to read from a graph — and
/// that is close to what this library shipped before.
/// </remarks>
public sealed class SourceChunkSelectionTests
{
    /// <remarks>
    /// The primary key. Every chunk belonging to the first selected entity precedes every chunk of
    /// the second, whatever either says.
    /// </remarks>
    [Fact]
    public void EveryChunkOfTheFirstEntityPrecedesEveryChunkOfTheSecond()
    {
        var inputs = new LocalSearchInputs
        {
            SelectedEntities =
            [
                new GraphEntity("FIRST", "Thing", "d") { SourceChunkIds = ["a_0", "a_1"] },
                new GraphEntity("SECOND", "Thing", "d") { SourceChunkIds = ["b_0", "b_1"] },
            ],
            SourceChunks = Chunks(("a_0", "a zero"), ("a_1", "a one"), ("b_0", "b zero"), ("b_1", "b one")),
        };

        var selected = SourceChunkSelection.Select(inputs);

        Assert.Equal(["a zero", "a one", "b zero", "b one"], selected.Select(c => c.Text), StringComparer.Ordinal);
    }

    /// <remarks>
    /// The secondary key, within one entity's block: chunks that more of that entity's
    /// relationships were extracted from come first. This is upstream's <c>count_relationships</c>.
    /// </remarks>
    [Fact]
    public void WithinOneEntityChunksBackedByMoreRelationshipsComeFirst()
    {
        var inputs = new LocalSearchInputs
        {
            SelectedEntities =
            [
                new GraphEntity("E", "Thing", "d") { SourceChunkIds = ["c_0", "c_1", "c_2"] },
            ],
            Relationships =
            [
                new GraphRelationship("E", "X", "r") { SourceChunkIds = ["c_2"] },
                new GraphRelationship("E", "Y", "r") { SourceChunkIds = ["c_2"] },
                new GraphRelationship("E", "Z", "r") { SourceChunkIds = ["c_1"] },
            ],
            SourceChunks = Chunks(("c_0", "none"), ("c_1", "one"), ("c_2", "two")),
        };

        var selected = SourceChunkSelection.Select(inputs);

        Assert.Equal(["two", "one", "none"], selected.Select(c => c.Text), StringComparer.Ordinal);
    }

    /// <remarks>
    /// A chunk shared by two entities belongs to the first one's block, not to both. Otherwise the
    /// primary key would mean nothing — a chunk would appear twice, in two places, at two ranks.
    /// </remarks>
    [Fact]
    public void ASharedChunkBelongsToTheFirstEntityThatClaimsIt()
    {
        var inputs = new LocalSearchInputs
        {
            SelectedEntities =
            [
                new GraphEntity("FIRST", "Thing", "d") { SourceChunkIds = ["shared_0"] },
                new GraphEntity("SECOND", "Thing", "d") { SourceChunkIds = ["shared_0", "own_0"] },
            ],
            SourceChunks = Chunks(("shared_0", "shared"), ("own_0", "own")),
        };

        var selected = SourceChunkSelection.Select(inputs);

        Assert.Equal(["shared", "own"], selected.Select(c => c.Text), StringComparer.Ordinal);
    }

    /// <remarks>
    /// <b>The degradation this library actually ships with today.</b> No relationship written
    /// before <see cref="GraphRelationship.SourceChunkIds"/> existed carries chunk provenance, so
    /// the secondary key reads 0 for everything. That must leave the entity order intact rather
    /// than scrambling it — a graph that cannot rank within a block still ranks the blocks.
    /// </remarks>
    [Fact]
    public void WithoutRelationshipProvenanceTheEntityOrderStillHolds()
    {
        var inputs = new LocalSearchInputs
        {
            SelectedEntities =
            [
                new GraphEntity("FIRST", "Thing", "d") { SourceChunkIds = ["a_0", "a_1"] },
                new GraphEntity("SECOND", "Thing", "d") { SourceChunkIds = ["b_0"] },
            ],
            Relationships = [new GraphRelationship("FIRST", "SECOND", "r")],
            SourceChunks = Chunks(("a_0", "a zero"), ("a_1", "a one"), ("b_0", "b zero")),
        };

        var selected = SourceChunkSelection.Select(inputs);

        Assert.Equal(["a zero", "a one", "b zero"], selected.Select(c => c.Text), StringComparer.Ordinal);
    }

    /// <remarks>
    /// An entity naming a chunk that is no longer in the store — its document deleted since
    /// extraction — is skipped. A stale id is not a reason to fail a query.
    /// </remarks>
    [Fact]
    public void AChunkIdThatNoLongerResolvesIsSkipped()
    {
        var inputs = new LocalSearchInputs
        {
            SelectedEntities =
            [
                new GraphEntity("E", "Thing", "d") { SourceChunkIds = ["gone_0", "here_0"] },
            ],
            SourceChunks = Chunks(("here_0", "still here")),
        };

        var selected = SourceChunkSelection.Select(inputs);

        Assert.Equal(["still here"], selected.Select(c => c.Text), StringComparer.Ordinal);
    }

    /// <summary>Builds a chunk lookup from id/text pairs.</summary>
    /// <param name="pairs">Chunk id and its text.</param>
    /// <returns>Chunks by id.</returns>
    private static Dictionary<string, TextChunk> Chunks(params (string Id, string Text)[] pairs)
    {
        var chunks = new Dictionary<string, TextChunk>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i++)
        {
            chunks[pairs[i].Id] = new TextChunk
            {
                Text = pairs[i].Text,
                DocumentId = new DocumentId("doc"),
                ChunkIndex = i,
            };
        }

        return chunks;
    }
}
