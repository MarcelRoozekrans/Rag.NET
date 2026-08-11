using System.Globalization;
using BenchmarkDotNet.Attributes;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;

namespace Rag.NET.Benchmarks;

/// <summary>
/// Benchmarks <see cref="InMemoryVectorStore.SearchAsync"/> — the dense retrieval path every
/// published latency figure runs through, and the one place this suite never looked.
/// <para>
/// <b>Why it did not exist.</b> Ten benchmarks in this project already call a <c>SearchAsync</c>,
/// and every one of them is a stub written to hold the store still while some other component is
/// measured. That is the right thing for those benchmarks and it left the real store with no
/// coverage at all: the library comparison measures this exact call, so the one path with a
/// published number attached was the one path with no allocation profile.
/// </para>
/// <para>
/// <b>Corpus sizes are the measured ones</b>, not round numbers — SciFact 5,183, ArguAna 8,674 and
/// FiQA 57,638 documents at one chunk per document, so a result here lines up with a row in
/// <c>docs/reference/library-comparison.md</c> rather than needing to be extrapolated onto it.
/// The vectors are 384-dimensional because the pinned comparison embedder is all-MiniLM-L6-v2.
/// </para>
/// <para>
/// Vectors are pseudo-random from a fixed seed: the scan is exhaustive and data-independent — no
/// index, no early exit, and the only branch is <c>MinScore</c>, which is left at its default of 0
/// so nothing is skipped. What matters is that the set is identical across runs and across any
/// change being evaluated, which a fixed seed gives and real corpus vectors would not add to.
/// </para>
/// </summary>
[MemoryDiagnoser]
public class VectorSearchBenchmarks
{
    /// <summary>all-MiniLM-L6-v2's width, so the inner loop is the length it really runs at.</summary>
    private const int Dimension = 384;

    /// <summary>The metric cutoff the comparison retrieves at, over-shot by one as ArguAna does.</summary>
    private const int TopK = 11;

    private InMemoryVectorStore _store = null!;
    private ReadOnlyMemory<float> _query;
    private SearchOptions _options = null!;

    /// <summary>The measured corpus sizes: SciFact, ArguAna, FiQA.</summary>
    [Params(5_183, 8_674, 57_638)]
    public int Documents { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var random = new Random(20260811);
        _store = new InMemoryVectorStore();
        _options = new SearchOptions { TopK = TopK };
        _query = RandomVector(random);

        // Stored in one call: the write path is not what this measures, and a per-document call
        // would take the store's write lock 57,638 times for no benchmark's benefit.
        var chunks = new EmbeddedChunk[Documents];
        for (var i = 0; i < Documents; i++)
        {
            chunks[i] = new EmbeddedChunk
            {
                Chunk = new TextChunk
                {
                    DocumentId = new DocumentId(
                        "doc-" + i.ToString(CultureInfo.InvariantCulture)),
                    ChunkIndex = 0,
                    Text = string.Empty,
                },
                Embedding = RandomVector(random),
            };
        }

        await _store.StoreAsync(chunks, CancellationToken.None);
    }

    [Benchmark]
    public async Task<IReadOnlyList<SearchResult>> Search() =>
        await _store.SearchAsync(_query, _options, CancellationToken.None);

    private static ReadOnlyMemory<float> RandomVector(Random random)
    {
        var vector = new float[Dimension];
        foreach (ref var component in vector.AsSpan())
        {
            component = (float)((random.NextDouble() * 2) - 1);
        }

        return vector;
    }
}
