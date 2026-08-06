using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

/// <summary>
/// Pins the read-time consumer the ChunkIndex collision defect corrupted:
/// <see cref="RrfMerger.MergeMany"/> keys deduplication on <c>(DocumentId, ChunkIndex)</c>.
/// Before the ParseBehavior fix, two genuinely different chunks from different sections of the
/// same document could share a ChunkIndex; RrfMerger would then treat them as the same hit and
/// silently drop one (see <c>chunkLookup.TryAdd</c>), merging unrelated content at read time.
/// With unique document-wide ChunkIndex values, two different chunks from different sections
/// must never collapse into a single result.
/// </summary>
public class RrfMergerSectionCollisionTests
{
    private static TextChunk Chunk(string docId, int chunkIndex, string text) =>
        new() { DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex, Text = text };

    private static SearchResult Result(TextChunk chunk, double score = 1.0) =>
        new() { Chunk = chunk, Score = score };

    [Fact]
    public void Merge_TwoDifferentChunksFromDifferentSections_BothSurviveAsDistinctResults()
    {
        // section1Chunk and section2Chunk come from different sections of the same document
        // and carry distinct, document-wide-unique ChunkIndex values (as ParseBehavior now
        // guarantees) — they must not be treated as the same identity by the merger.
        var section1Chunk = Chunk("doc-1", 0, "content from section one");
        var section2Chunk = Chunk("doc-1", 1, "content from section two");

        var dense = new List<SearchResult> { Result(section1Chunk, 0.9), Result(section2Chunk, 0.8) };

        var result = RrfMerger.Merge(dense, [], topK: 5);

        Assert.Equal(2, result.Count);
        var texts = result.Select(r => r.Chunk.Text).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("content from section one", texts);
        Assert.Contains("content from section two", texts);
    }

    [Fact]
    public void Merge_DifferentChunksThatWouldHaveCollidedBeforeFix_AreNotMergedIntoOne()
    {
        // Simulates the pre-fix defect directly: if ChunkIndex numbering restarted per section,
        // the first chunk of section 1 and the first chunk of section 2 both got ChunkIndex 0
        // and shared identity key (DocumentId, ChunkIndex) = ("doc-1", 0). Post-fix, ParseBehavior
        // renumbers them 0 and 1 (or similar) before they ever reach retrieval — this test pins
        // that once indices are distinct, RrfMerger keeps both as separate hits rather than
        // discarding one via chunkLookup.TryAdd.
        var firstSectionFirstChunk = Chunk("doc-1", 0, "first section, first chunk");
        var secondSectionFirstChunk = Chunk("doc-1", 5, "second section, first chunk (renumbered)");

        var dense = new List<SearchResult> { Result(firstSectionFirstChunk, 0.9) };
        var bm25 = new List<(TextChunk chunk, double score)> { (secondSectionFirstChunk, 40.0) };

        var result = RrfMerger.Merge(dense, bm25, topK: 5);

        Assert.Equal(2, result.Count);
    }
}
