using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Search;

/// <summary>
/// The dense arm and the BM25 arm must agree about which chunks a filter matches. They are
/// separate <see cref="Rag.NET.Abstractions.IBm25Index"/>/<see cref="Rag.NET.Abstractions.IVectorStore"/>
/// implementations reached by separate code paths in client-side hybrid search; if they ever
/// disagree, a filtered query returns a different set of chunks depending on which arm found them
/// (#350).
/// </summary>
/// <remarks>
/// This drives both real arms rather than comparing <see cref="InMemoryBm25Index.Search"/> against
/// <see cref="Rag.NET.Abstractions.MetadataFilterMatcher.Matches"/> directly: <c>Search</c> calls
/// <c>Matches</c> internally, so that comparison could never observe the two arms diverging — it
/// would stay green even if someone re-inlined a private matcher into <see cref="InMemoryVectorStore"/>
/// that disagreed with the shared one. Comparing the two real arms' outputs is what actually
/// exercises the risk this test exists to catch.
/// </remarks>
public sealed class MetadataFilterParityTests
{
    // Local helper rather than reaching into InMemoryBm25IndexTests or EnsembleBehaviorTests,
    // which each define their own small variant of this — the accepted cost of keeping test
    // classes independent instead of introducing a shared test utility for three call sites.
    private static TextChunk FilterChunk(int index, string text, string tenant) =>
        new()
        {
            DocumentId = new DocumentId("doc-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ChunkIndex = index,
            Text = text,
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["tenant"] = tenant,
            },
        };

    [Fact]
    public async Task DenseAndBm25Arms_AgreeOnWhichChunksMatchAFilter()
    {
        var ct = TestContext.Current.CancellationToken;

        var chunks = new[]
        {
            FilterChunk(1, "alpha term", "a"),
            FilterChunk(2, "beta term", "b"),
            FilterChunk(3, "gamma term", "a"),
        };

        var filter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
        {
            ["tenant"] = "a",
        };

        // Identical embeddings so the dense arm's candidate set, with TopK = 10 over three
        // chunks, is about filtering rather than ranking -- every chunk is an equally-ranked
        // candidate before the filter is applied.
        var vector = new ReadOnlyMemory<float>([1f, 0f, 0f]);

        using var vectorStore = new InMemoryVectorStore();
        await vectorStore.StoreAsync(
            [.. chunks.Select(chunk => new EmbeddedChunk { Chunk = chunk, Embedding = vector })],
            ct);

        using var bm25 = new InMemoryBm25Index();
        for (var i = 0; i < chunks.Length; i++)
            bm25.Add(i + 1, chunks[i]);

        var denseMatched = (await vectorStore.SearchAsync(vector, new SearchOptions { TopK = 10, MetadataFilter = filter }, ct))
            .Select(static result => result.Chunk.ChunkIndex)
            .OrderBy(static index => index)
            .ToArray();

        var bm25Matched = bm25.Search("term", topK: 10, metadataFilter: filter)
            .Select(static hit => hit.chunk.ChunkIndex)
            .OrderBy(static index => index)
            .ToArray();

        Assert.Equal(denseMatched, bm25Matched);
    }
}
