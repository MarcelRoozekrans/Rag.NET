using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Guards the seam that lets a cell index units whose embeddings it produced itself, rather than
/// letting the harness embed their text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this seam needs a guard rather than just a parameter.</b> Late chunking's whole claim is
/// that a chunk's vector carries whole-document token context. The harness has always embedded a
/// unit's <i>text</i> through the sentence embedder, which for late-chunked units would produce a
/// cell measuring <b>late chunking's boundaries with ordinary embeddings</b> — a number that looks
/// plausible, reproduces perfectly across runs, and answers a question nobody asked. A silent
/// fallback here is not a smaller version of the right measurement; it is a different measurement
/// wearing its name.
/// </para>
/// <para>
/// So the contract is checked rather than trusted: a source that promises pre-computed embeddings
/// must supply one for <b>every</b> unit, and a missing or empty vector throws naming the unit. The
/// alternative — embedding that one unit's text and carrying on — is the failure this whole seam
/// exists to make impossible.
/// </para>
/// </remarks>
public sealed class PrecomputedEmbeddingsTests
{
    [Fact]
    public void RequireEmbedding_WhenEveryUnitCarriesOne_ReturnsThemInOrder()
    {
        var units = new[]
        {
            UnitWith("a", [1f, 0f]),
            UnitWith("b", [0f, 1f]),
        };

        var vectors = BeirHarness.RequirePrecomputedEmbeddings(units);

        Assert.Equal(2, vectors.Count);
        Assert.Equal(1f, vectors[0].Span[0]);
        Assert.Equal(1f, vectors[1].Span[1]);
    }

    [Fact]
    public void RequireEmbedding_WhenOneUnitHasNone_ThrowsNamingIt()
    {
        // The case that matters. A late-chunking run whose generator failed on one section falls
        // back to a null embedding by design -- EmbeddingBehavior backfills it in production, which
        // is correct there and catastrophic here, because the backfill is an ORDINARY embedding and
        // the cell would report it as a late-chunked one.
        var units = new[]
        {
            UnitWith("a", [1f, 0f]),
            UnitWith("b", null),
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => BeirHarness.RequirePrecomputedEmbeddings(units));

        Assert.Contains("b", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("1 of 2", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireEmbedding_WhenOneUnitHasAnEmptyVector_ThrowsToo()
    {
        // Empty is the shape a failed generator actually produces here, and it is not null, so a
        // null check alone would let it through to be indexed as a zero-length vector -- which the
        // store would either reject or silently rank last, and neither is a measurement.
        var units = new[]
        {
            UnitWith("a", [1f, 0f]),
            UnitWith("b", []),
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => BeirHarness.RequirePrecomputedEmbeddings(units));

        Assert.Contains("b", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireEmbedding_WhenDimensionsDisagree_ThrowsRatherThanIndexingThem()
    {
        // Two dimensions in one index is not a retrievable corpus, and the failure it produces
        // downstream is a cosine over mismatched spans rather than anything naming the cause.
        var units = new[]
        {
            UnitWith("a", [1f, 0f]),
            UnitWith("b", [0f, 1f, 0f]),
        };

        var thrown = Assert.Throws<InvalidOperationException>(
            () => BeirHarness.RequirePrecomputedEmbeddings(units));

        Assert.Contains("dimension", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexAsync_WhenUnitsCarryTheirOwnEmbeddings_StoresThoseAndNeverConsultsTheEmbedder()
    {
        // The wiring test, and it is the one that matters. The three tests above prove the CHECKER
        // works; none of them proves IndexAsync calls it. Replacing the branch with a hardcoded
        // null -- the exact edit a refactor might make -- compiles and leaves every one of them
        // green, while a late-chunking cell silently falls back to embedding chunk text. That is
        // the failure this whole seam exists to prevent, so it needs a test that fails when the
        // wiring goes rather than when the checker does.
        //
        // The generator and cache are passed as null deliberately. On the precomputed path they
        // must not be touched, and null is the only argument that proves it: a stub would record
        // no call and pass just as well if the path quietly stopped needing one, whereas a null
        // dereference is unambiguous. This doubles as the assertion that a precomputed cell needs
        // no embedder at all.
        var ct = TestContext.Current.CancellationToken;
        var units = new[]
        {
            UnitWith("near", [1f, 0f]),
            UnitWith("far", [0f, 1f]),
        };

        using var store = new InMemoryVectorStore();
        await BeirHarness.IndexAsync(
            generator: null!,
            embeddings: null!,
            store,
            units,
            unitsCarryTheirOwnEmbeddings: true,
            ct);

        // Searching along the first unit's axis must return it first. If IndexAsync had embedded
        // the TEXT "near" and "far" instead, these vectors would be whatever the embedder produced
        // for those words and the assertion below would not hold by construction.
        var hits = await store.SearchAsync(
            new float[] { 1f, 0f }, new SearchOptions { TopK = 2 }, ct);

        Assert.Equal(2, hits.Count);
        Assert.Equal("near", hits[0].Chunk.DocumentId.Value);
        Assert.Equal(1f, hits[0].Score, 5);
        Assert.Equal("far", hits[1].Chunk.DocumentId.Value);
        Assert.Equal(0f, hits[1].Score, 5);
    }

    [Fact]
    public void Partition_KeepsEmbeddedUnitsAndNamesTheExcludedDocuments()
    {
        // 1 of 300 -- 0.33%, inside the ceiling, and close to the 0.21% SciFact actually produces.
        // A three-unit fixture would be 33% and would trip the systemic guard below, which is the
        // guard working rather than a fixture worth keeping.
        var units = new List<TextChunk> { UnitWith("kept", [1f, 0f]), UnitWith("dropped", null) };
        for (var i = 0; i < 298; i++)
        {
            units.Add(UnitWith($"filler-{i}", [0f, 1f]));
        }

        var (kept, excluded) = BeirRealChunkingTests.PartitionLateChunked(units);

        Assert.Equal(299, kept.Count);
        Assert.Equal("kept", kept[0].DocumentId.Value);
        Assert.Equal(["dropped"], excluded);
    }

    [Fact]
    public void Partition_WhenTooMuchOfTheCorpusIsMissing_ThrowsRatherThanReportingATail()
    {
        // The distinction this encodes, and it is the whole reason exclusion is safe at all. A
        // handful of documents carrying a control character BERT itself deletes is a documented
        // tail; 14.7% of the corpus is a systemic failure wearing the same shape. The first
        // late-chunking run on SciFact produced exactly the second -- 1,401 of 9,506 units, caused
        // by a MaxTokens default the model could not honour -- and a partition that quietly
        // excluded them would have reported a figure over 85% of the corpus and called it a
        // measurement.
        var units = new List<TextChunk>();
        for (var i = 0; i < 100; i++)
        {
            units.Add(UnitWith($"doc-{i}", i < 90 ? [1f, 0f] : null));
        }

        var thrown = Assert.Throws<InvalidOperationException>(
            () => BeirRealChunkingTests.PartitionLateChunked(units));

        Assert.Contains("10 of 100", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("systemic", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TextChunk UnitWith(string id, float[]? embedding) => new()
    {
        Text = id,
        DocumentId = new DocumentId(id),
        ChunkIndex = 0,
        Embedding = embedding is null ? null : new ReadOnlyMemory<float>(embedding),
    };
}
