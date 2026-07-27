using System.Diagnostics;
using System.Globalization;
using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

/// <summary>
/// <c>StorageBehavior</c> calls <see cref="InMemoryBm25Index.Remove"/> before every ingest so
/// that re-ingest is a replace. That puts <c>Remove</c> on the first-time-ingest path too, where
/// there is nothing to remove — so an absent document has to cost O(1). It used to cost a scan
/// of every indexed chunk plus a <c>RemoveAll</c> over every postings list, making bulk ingest
/// quadratic in the number of documents.
/// </summary>
public class InMemoryBm25IndexRemoveScalingTests
{
    private const int DocumentCount = 1000;
    private const int ChunksPerDocument = 2;
    private const int TermsPerChunk = 30;

    private static InMemoryBm25Index BuildIndex(out int nextDocId)
    {
        var index = new InMemoryBm25Index();
        var rng = new Random(20260727);
        var docId = 0;

        for (var d = 0; d < DocumentCount; d++)
        {
            for (var c = 0; c < ChunksPerDocument; c++)
            {
                var terms = new List<string>(TermsPerChunk);
                for (var t = 0; t < TermsPerChunk; t++)
                    terms.Add("term" + rng.Next(2000).ToString(CultureInfo.InvariantCulture));

                index.Add(++docId, new TextChunk
                {
                    Text = string.Join(' ', terms),
                    DocumentId = new DocumentId("doc-" + d.ToString(CultureInfo.InvariantCulture)),
                    ChunkIndex = c,
                });
            }
        }

        nextDocId = docId;
        return index;
    }

    /// <summary>
    /// Bulk ingest stays linear: removing documents that were never indexed must not touch the
    /// index at all. The bound is deliberately loose — the scanning implementation this replaced
    /// needed multiple seconds for this workload, while a dictionary lookup per call is
    /// sub-millisecond in total, so the margin is orders of magnitude and not a tuned threshold.
    /// </summary>
    [Fact]
    public void Remove_AbsentDocument_DoesNotScanTheIndex()
    {
        using var index = BuildIndex(out _);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++)
            index.Remove("absent-" + i.ToString(CultureInfo.InvariantCulture));
        sw.Stop();

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"5000 removals of absent documents took {sw.ElapsedMilliseconds}ms — Remove is scanning the index again");

        // Still intact: the no-op removals changed nothing.
        Assert.NotEmpty(index.Search("term1", topK: 5));
    }

    /// <summary>The document-id map must track removals, or a removed document stays "present".</summary>
    [Fact]
    public void Remove_ThenReAdd_ReindexesTheDocument()
    {
        using var index = new InMemoryBm25Index();
        index.Add(1, new TextChunk { Text = "quokka telemetry", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });

        index.Remove("doc-1");
        Assert.Empty(index.Search("quokka", topK: 5));

        // A fresh internal doc id, exactly as StorageBehavior supplies on a re-ingest.
        index.Add(2, new TextChunk { Text = "quokka telemetry", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });
        Assert.Single(index.Search("quokka", topK: 5));

        // And removing again finds it under its new id.
        index.Remove("doc-1");
        Assert.Empty(index.Search("quokka", topK: 5));
    }

    /// <summary>The document-id map must be cleared with everything else.</summary>
    [Fact]
    public async Task ClearAsync_ThenReAdd_LeavesNoStaleDocumentIds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var index = new InMemoryBm25Index();
        index.Add(1, new TextChunk { Text = "quokka telemetry", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });

        await index.ClearAsync(ct);
        index.Add(2, new TextChunk { Text = "quokka telemetry", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 });

        Assert.Single(index.Search("quokka", topK: 5));

        index.Remove("doc-1");
        Assert.Empty(index.Search("quokka", topK: 5));
    }

    /// <summary>Every chunk of a multi-chunk document is tracked, not just the first.</summary>
    [Fact]
    public void Remove_MultiChunkDocument_RemovesEveryChunk()
    {
        using var index = new InMemoryBm25Index();
        for (var i = 0; i < 5; i++)
        {
            index.Add(i + 1, new TextChunk
            {
                Text = "quokka telemetry chunk " + i.ToString(CultureInfo.InvariantCulture),
                DocumentId = new DocumentId("doc-1"),
                ChunkIndex = i,
            });
        }

        index.Add(99, new TextChunk { Text = "quokka elsewhere", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0 });

        Assert.Equal(6, index.Search("quokka", topK: 50).Count);

        index.Remove("doc-1");

        var remaining = index.Search("quokka", topK: 50);
        Assert.Single(remaining);
        Assert.Equal("doc-2", remaining[0].chunk.DocumentId.Value);
    }
}
