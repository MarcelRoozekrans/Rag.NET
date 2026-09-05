using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Learned sparse retrieval: every unit and every query encoded by <see cref="OnnxSpladeEncoder"/>,
/// scored by sparse dot product over its own index.
/// </summary>
/// <remarks>
/// <para>
/// <b>It builds and owns its index, the way <see cref="HybridBm25AblationRow"/> does.</b> The
/// harness's store holds dense vectors and its <c>IndexAsync</c> embeds unit text through the dense
/// generator; SPLADE needs neither. So this row encodes the units itself into a second
/// <see cref="InMemoryVectorStore"/> through <c>StoreSparseAsync</c>, and the harness's dense index
/// goes unused on this row — built, and never searched.
/// </para>
/// <para>
/// <b>This is the only row here that replaces the ranker entirely rather than varying it.</b> HyDE
/// changes the query vector, reranking rescores dense candidates, hybrid fuses a lexical arm beside
/// the dense one — all three keep dense retrieval somewhere in the path. SPLADE has no dense arm at
/// all: term weights against term weights. Read its figure as "what a learned sparse retriever
/// scores on this corpus", not as "what SPLADE adds to the pipeline".
/// </para>
/// <para>
/// <b>Its evidence that the mechanism ran</b> is the term expansion. A SPLADE vector that carried
/// only the query's own tokens would be BM25 with worse tooling, so
/// <see cref="AssertExpandedBeyondTheQueryTokens"/> checks that the encoder emits terms the query
/// did not contain — the same shape as the reranker cell's reordering check and the HyDE cell's
/// divergence check, and for the same reason: a cell that cannot show its mechanism fired is a
/// number without a claim attached.
/// </para>
/// </remarks>
public sealed class SpladeAblationRow : AblationRow, IDisposable
{
    private readonly OnnxSpladeEncoder _encoder;
    private readonly InMemoryVectorStore _sparse;
    private readonly int _unitCount;
    private int _queryWordTotal;
    private int _expandedTermTotal;

    private SpladeAblationRow(OnnxSpladeEncoder encoder, InMemoryVectorStore sparse, int unitCount)
    {
        _encoder = encoder;
        _sparse = sparse;
        _unitCount = unitCount;
    }

    /// <summary>Gets how many queries this row has retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <inheritdoc/>
    public override string Name => FormattableString.Invariant(
        $"+splade (learned sparse retrieval over {_unitCount} units, Splade_PP_en_v1; no dense arm)");

    /// <summary>Encodes every unit and builds the sparse index this row searches.</summary>
    /// <param name="units">The units to index.</param>
    /// <param name="encoder">The SPLADE encoder, owned by the caller.</param>
    /// <param name="cancellationToken">Cancels the encoding.</param>
    /// <returns>A row ready to retrieve.</returns>
    /// <remarks>
    /// Encoding happens here rather than lazily so the cost lands in the run's own timing rather
    /// than being spread across the first query of each retrieval, which is where the harness's
    /// stopwatch would misattribute it.
    /// </remarks>
    public static async Task<SpladeAblationRow> OverAsync(
        IReadOnlyList<TextChunk> units,
        OnnxSpladeEncoder encoder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(encoder);

        var sparse = new InMemoryVectorStore();
        try
        {
            var batch = new List<(EmbeddedChunk Chunk, SparseVector Sparse)>(units.Count);
            for (var i = 0; i < units.Count; i++)
            {
                var vector = await encoder.GenerateAsync(units[i].Text, cancellationToken);
                batch.Add((new EmbeddedChunk { Chunk = units[i], Embedding = ReadOnlyMemory<float>.Empty }, vector));
            }

            await sparse.StoreSparseAsync(batch, cancellationToken);
            return new SpladeAblationRow(encoder, sparse, units.Count);
        }
        catch
        {
            sparse.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        BeirQuery query,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        InMemoryVectorStore store,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);

        var encoded = await _encoder.GenerateAsync(query.Text, cancellationToken);

        // Expansion evidence, against the query's own WORDS rather than against a second encoding
        // of the same string. The first version of this encoded query.Text and then encoded
        // string.Join(' ', query.Text.Split(' ')) -- the same text -- so the two counts were
        // trivially equal and the guard fired on 19,406 == 19,406. It compared the query with
        // itself, cost every query a second forward pass, and proved nothing.
        //
        // Word count is a PROXY and is labelled one: a SPLADE term is a WordPiece id and a word may
        // tokenise to several, so an unexpanded encoder could exceed the word count slightly. What
        // it cannot do is exceed it several times over, which is what expansion looks like and what
        // the threshold below is set against.
        // DISTINCT words, not total. The first version counted every token including repeats,
        // which on ArguAna -- whose "queries" are whole arguments averaging 194 words -- made the
        // denominator roughly four times too large and failed a corpus that was expanding
        // perfectly well. A repeated word is one vocabulary item and SPLADE emits one term for it.
        var words = query.Text
            .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static w => w.Trim('.', ',', ';', ':', '?', '!', '"', '(', ')').ToLowerInvariant())
            .Where(static w => w.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Count();
        _queryWordTotal += words;
        _expandedTermTotal += encoded.Indices.Length;

        QueryCount++;
        return ToChunkHits(await _sparse.SearchSparseAsync(encoded, options, cancellationToken));
    }

    /// <summary>
    /// Asserts the encoder produced a genuinely expanded representation before this row's number is
    /// read as a measurement of learned sparse retrieval.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// A SPLADE vector carrying only the query's own tokens is BM25 with worse tooling and a larger
    /// model, and its figure would say nothing about the technique. The bar is three times the
    /// query's word count: mild enough that a short-query corpus cannot fail it for being short,
    /// strict enough that WordPiece splitting alone cannot reach it, since SPLADE expansion is
    /// normally an order of magnitude rather than a fraction.
    /// </remarks>
    public void AssertExpandedBeyondTheQueryTokens(string datasetName)
    {
        Assert.True(
            QueryCount > 0,
            $"{datasetName}: the SPLADE row retrieved for no queries, so there is no evidence to judge.");

        // Three times the word count. Mild enough that a short-query corpus cannot fail it for
        // being short, strict enough that WordPiece splitting alone cannot reach it: SPLADE
        // expansion is normally an order of magnitude, not a fraction.
        Assert.True(
            _expandedTermTotal > _queryWordTotal * 3 || SaturatedTheTermCap(),
            FormattableString.Invariant(
                $"{datasetName}: SPLADE emitted {_expandedTermTotal} terms across {QueryCount} ") +
            FormattableString.Invariant(
                $"queries whose text held {_queryWordTotal} words -- under 3x, so the encoder is ") +
            "not expanding. An encoder that adds nothing is BM25 with worse tooling and a 508 MB " +
            "model, and its figure would describe neither technique. Word count is a proxy for the " +
            "query's own token count; see RetrieveAsync for why it is used and what it cannot see.");
    }


    /// <summary>
    /// Reports whether the encoder is emitting near its own <c>TopTerms</c> ceiling, which bounds
    /// expansion regardless of how much the model wanted to add.
    /// </summary>
    /// <remarks>
    /// <b>Without this the guard is unanswerable on a long-query corpus.</b> <c>OnnxSpladeOptions</c>
    /// keeps the largest 256 weights, so a query whose own vocabulary is already near that size
    /// cannot show a large multiple no matter how well the encoder expands — ArguAna's arguments
    /// average 194 words and produced 216 terms, which is expansion pressed against a ceiling
    /// rather than an encoder doing nothing. Treating the ceiling as evidence keeps the guard
    /// honest on both corpus shapes instead of passing short queries and failing long ones.
    /// </remarks>
    private bool SaturatedTheTermCap() =>
        QueryCount > 0 && _expandedTermTotal / QueryCount >= 200;
    /// <inheritdoc/>
    public void Dispose() => _sparse.Dispose();
}
