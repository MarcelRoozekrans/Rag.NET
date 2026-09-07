using System.Globalization;
using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class InMemoryBm25IndexTests
{
    [Fact]
    public void Search_ReturnsEmpty_WhenIndexIsEmpty()
    {
        var index = new InMemoryBm25Index();
        var results = index.Search("hello", topK: 5);
        Assert.Empty(results);
    }

    [Fact]
    public void Search_ReturnsMatchingDoc_WhenTermPresent()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "the quick brown fox", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "the lazy dog sleeps", DocumentId = new DocumentId("doc1"), ChunkIndex = 1 });

        var results = index.Search("fox", topK: 5);

        Assert.Single(results);
        Assert.Equal("doc1", results[0].chunk.DocumentId);
        Assert.Equal(0, results[0].chunk.ChunkIndex);
    }

    [Fact]
    public void Search_RanksHigherFrequencyTermHigher()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "cat cat cat", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "cat dog bird", DocumentId = new DocumentId("doc1"), ChunkIndex = 1 });

        var results = index.Search("cat", topK: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].chunk.ChunkIndex); // higher TF should rank first
    }

    [Fact]
    public void Search_RespectsTopK()
    {
        var index = new InMemoryBm25Index();
        for (int i = 0; i < 10; i++)
            index.Add(new TextChunk { Text = "hello world", DocumentId = new DocumentId("doc1"), ChunkIndex = i });

        var results = index.Search("hello", topK: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Remove_DeletesAllChunksForDocument()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "hello world", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "hello universe", DocumentId = new DocumentId("doc2"), ChunkIndex = 0 });

        index.Remove("doc1");

        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
        Assert.Equal("doc2", results[0].chunk.DocumentId);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "Hello World", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });

        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
    }

    [Fact]
    public void Search_IgnoresPunctuation()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "fox! jumps... over, the fence.", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });

        var results = index.Search("jumps", topK: 5);
        Assert.Single(results);
    }

    [Fact]
    public void Search_ReturnsEmpty_WhenTopKIsZero()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "hello world", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        var results = index.Search("hello", topK: 0);
        Assert.Empty(results);
    }

    [Fact]
    public void Remove_OnNonExistentDocument_IsNoOp()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "hello world", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Remove("does-not-exist"); // should not throw
        var results = index.Search("hello", topK: 5);
        Assert.Single(results);
    }

    [Fact]
    public void Search_MultiWordQuery_AccumulatesScoresAcrossTerms()
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "quick brown fox", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "quick lazy dog", DocumentId = new DocumentId("doc2"), ChunkIndex = 0 });

        // "doc1" (quick brown fox) has both "quick" and "fox"; "doc2" only has "quick"
        var results = index.Search("quick fox", topK: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc1", results[0].chunk.DocumentId); // "doc1" ranks higher — matches both terms
    }

    // Gap 1 — empty/whitespace query returns empty results
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_EmptyOrWhitespaceQuery_ReturnsEmpty(string query)
    {
        var index = new InMemoryBm25Index();
        index.Add(new TextChunk { Text = "hello world", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "quick brown fox", DocumentId = new DocumentId("doc1"), ChunkIndex = 1 });

        var results = index.Search(query, topK: 5);

        Assert.Empty(results);
    }

    /// <summary>A supplied duplicate id throws instead of dropping the chunk.</summary>
    /// <remarks>
    /// <para>
    /// <b>Replaces <c>Add_DuplicateDocId_IsIdempotent</c>, which asserted the behaviour that caused
    /// #490.</b> Callers used to supply ids and a duplicate returned silently — "idempotent" was a
    /// fair description of the code and the wrong contract for the caller. Because
    /// <c>PipelineIngestor</c> allocated from a counter that restarted at 0 each process while a
    /// persisted index reloads the ids it holds, every document ingested after a restart hit that
    /// silent return and was simply missing from keyword and hybrid search.
    /// </para>
    /// <para>
    /// The public <see cref="InMemoryBm25Index.Add"/> assigns ids itself, so it cannot collide.
    /// <c>AddWithId</c> exists for the one path that legitimately supplies one — the SQLite index
    /// restoring what it persisted — and a duplicate there means a caller reintroduced the defect.
    /// </para>
    /// </remarks>
    [Fact]
    public void AddWithId_DuplicateId_Throws()
    {
        using var index = new InMemoryBm25Index();
        var chunk = new TextChunk { Text = "hello world", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 };

        index.AddWithId(7, chunk);

        var ex = Assert.Throws<InvalidOperationException>(() => index.AddWithId(7, chunk));
        Assert.Contains("already indexed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Allocated ids never revisit one supplied through <c>AddWithId</c>.</summary>
    /// <remarks>
    /// The seeding half of the #490 fix, asserted directly: restoring persisted ids must push the
    /// allocator past them, or the next allocation collides with what was just reloaded.
    /// </remarks>
    [Fact]
    public void Add_AfterAddWithId_DoesNotReuseTheSuppliedId()
    {
        using var index = new InMemoryBm25Index();

        index.AddWithId(42, new TextChunk { Text = "restored", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        var allocated = index.Add(new TextChunk { Text = "fresh", DocumentId = new DocumentId("doc2"), ChunkIndex = 0 });

        Assert.True(allocated > 42, $"allocator returned {allocated}, which revisits the restored range");
        Assert.Equal(2, index.Search("restored fresh", topK: 10).Count);
    }

    // Gap 3 — IDF boundary when df == N: score must still be positive
    [Fact]
    public void Search_AllDocsContainTerm_ScoreIsPositive()
    {
        var index = new InMemoryBm25Index();
        // All 3 documents contain "common" → df == N == 3
        index.Add(new TextChunk { Text = "common ground", DocumentId = new DocumentId("doc1"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "common sense", DocumentId = new DocumentId("doc2"), ChunkIndex = 0 });
        index.Add(new TextChunk { Text = "common people", DocumentId = new DocumentId("doc3"), ChunkIndex = 0 });

        var results = index.Search("common", topK: 5);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.score > 0, "Score must be positive even when df == N"));
    }

    private static TextChunk FilterChunk(int index, string text, string tenant)
    {
        return new TextChunk
        {
            DocumentId = new DocumentId("doc-" + index.ToString(CultureInfo.InvariantCulture)),
            ChunkIndex = index,
            Text = text,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = tenant,
            },
        };
    }

    [Fact]
    public void Search_WithMetadataFilter_ExcludesNonMatchingChunks()
    {
        using var sut = new InMemoryBm25Index();
        sut.Add(FilterChunk(1, "shared search term", "a"));
        sut.Add(FilterChunk(2, "shared search term", "b"));

        var results = sut.Search(
            "shared search term",
            topK: 10,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }

    // The advantage of filtering inside the index over filtering the results afterwards: topK
    // comes back full of eligible hits rather than the best overall minus whatever was dropped.
    // With post-filtering, asking for 1 here would return 0 whenever the "b" chunk outranked
    // the "a" one.
    [Fact]
    public void Search_WithMetadataFilter_FillsTopKWithEligibleChunks()
    {
        using var sut = new InMemoryBm25Index();
        sut.Add(FilterChunk(1, "term term term term", "b"));
        sut.Add(FilterChunk(2, "term", "a"));

        var results = sut.Search(
            "term",
            topK: 1,
            metadataFilter: new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = "a",
            });

        var hit = Assert.Single(results);
        Assert.True(hit.chunk.Metadata["tenant"] == "a");
    }

    [Fact]
    public void Search_WithNullMetadataFilter_ReturnsEverything()
    {
        using var sut = new InMemoryBm25Index();
        sut.Add(FilterChunk(1, "shared search term", "a"));
        sut.Add(FilterChunk(2, "shared search term", "b"));

        var results = sut.Search("shared search term", topK: 10, metadataFilter: null);

        Assert.Equal(2, results.Count);
    }

    /// <summary>Ids are assigned by the index, and each one is distinct.</summary>
    /// <remarks>
    /// The property that makes #490 unrepeatable: a caller cannot supply an id, so a caller cannot
    /// collide with one. Before this, <c>PipelineIngestor</c> supplied them from a counter that
    /// restarted at 0 each process while a persisted index reloaded the ids it already held.
    /// </remarks>
    [Fact]
    public void Add_AssignsDistinctIds()
    {
        using var sut = new InMemoryBm25Index();

        var ids = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(sut.Add(new TextChunk
            {
                Text = $"chunk {i}",
                DocumentId = new DocumentId("doc-1"),
                ChunkIndex = i,
            }));
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
