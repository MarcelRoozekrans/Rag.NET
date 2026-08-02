using Rag.NET.Benchmarks.Quality;
using Rag.NET.Embeddings.Onnx;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The +HyDE row: the query's cached hypothetical answer passages, embedded and mean-pooled into
/// one L2-normalised search vector — <c>HydeBehavior</c>'s averaging exactly — which searches the
/// store in the query vector's place.
/// <para>
/// <b>No LLM call happens in this row, ever.</b> Hosted LLMs are not bit-deterministic at any
/// temperature, so the generation run is the experiment and it is frozen: this row reads it back
/// verbatim through a <see cref="HypotheticalCache"/> that the constructor requires to be in
/// <see cref="HypotheticalCacheMode.RefuseOnMiss"/> mode. A missing entry fails the run loudly,
/// naming the key and the tool that fills it — any quiet alternative would blend a second sampling
/// run into a table labelled as one, or worse, fall back to the query text and publish the dense
/// number under a HyDE label.
/// </para>
/// <para>
/// <b>The max-pool score trap the reranked row fell into does not apply here, by construction.</b>
/// This row does not reorder hits that carry another search's scores: it searches with a different
/// <i>vector</i>, so every hit's score is the cosine against the HyDE vector — the very quantity
/// its ranking is sorted by — and <c>DocumentRanking</c>'s max-pool over those scores preserves the
/// HyDE order rather than re-sorting it into the dense one.
/// </para>
/// <para>
/// <b>The row records whether the HyDE ranking ever differs from the dense ranking, and
/// <see cref="AssertHydeDiverged"/> turns silence into a failure.</b> If reading the hypotheticals
/// silently degraded to the query text, or the mean collapsed onto the query vector, the search
/// vector IS the dense vector and every ranking matches exactly — a passing test displaying the
/// dense number twice. The counters are per-instance and accumulate, so a row instance belongs to
/// one measurement.
/// </para>
/// </summary>
public sealed class HydeAblationRow : AblationRow
{
    private readonly HypotheticalCache _hypotheticals;
    private readonly int _hypothesisCount;

    /// <summary>Builds the row over its frozen generation run.</summary>
    /// <param name="hypotheticals">
    /// The cache the generation tool filled, opened in <see cref="HypotheticalCacheMode.RefuseOnMiss"/>
    /// mode — anything else is refused here, because a fill-mode cache would generate on a miss and
    /// generation is the one thing this row must never do.
    /// </param>
    /// <param name="hypothesisCount">
    /// How many hypotheses to average — <c>HydeOptions.HypothesisCount</c> from the same options
    /// the generation tool ran with, so the row reads exactly the indexes the tool wrote.
    /// </param>
    public HydeAblationRow(HypotheticalCache hypotheticals, int hypothesisCount)
    {
        ArgumentNullException.ThrowIfNull(hypotheticals);
        ArgumentOutOfRangeException.ThrowIfLessThan(hypothesisCount, 1);
        if (hypotheticals.Mode != HypotheticalCacheMode.RefuseOnMiss)
        {
            throw new ArgumentException(
                $"The hypothetical cache must be opened in {nameof(HypotheticalCacheMode)}." +
                $"{nameof(HypotheticalCacheMode.RefuseOnMiss)} mode. This row never generates " +
                "text: a fill-mode cache would quietly sample a second generation run on a miss " +
                "and blend it into a table whose HyDE row claims to measure one.",
                nameof(hypotheticals));
        }

        _hypotheticals = hypotheticals;
        _hypothesisCount = hypothesisCount;
    }

    /// <summary>Gets how many queries this row has retrieved for.</summary>
    public int QueryCount { get; private set; }

    /// <summary>
    /// Gets how many of those queries ended with a HyDE ranking that differs from the dense
    /// ranking — the only direct evidence that the hypothetical vector is not the query vector.
    /// </summary>
    public int DivergedQueryCount { get; private set; }

    /// <inheritdoc/>
    public override string Name => FormattableString.Invariant(
        $"+hyde (mean of {_hypothesisCount} cached {_hypotheticals.ModelIdentity} hypotheticals, L2-normalised; no LLM call — reads the frozen generation run)");

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<ChunkHit>> RetrieveAsync(
        BeirQuery query,
        OnnxEmbeddingGenerator generator,
        EmbeddingCache embeddings,
        InMemoryVectorStore store,
        SearchOptions options,
        CancellationToken cancellationToken)
    {
        // The dense leg runs too, but only as the guard's evidence: its ranking is what the HyDE
        // ranking is compared against, per query. The query embedding and both searches go through
        // the same cache and store as every other row's, so the comparison is like for like.
        var queryVectors = await BeirHarness.EmbedAsync(
            generator, embeddings, [query.Text], cancellationToken);
        var dense = ToChunkHits(await store.SearchAsync(queryVectors[0], options, cancellationToken));

        var hypotheticals = new string[_hypothesisCount];
        for (var i = 0; i < _hypothesisCount; i++)
        {
            hypotheticals[i] = await _hypotheticals.GetOrAddAsync(
                query.Text, i, RefuseToGenerate, cancellationToken);
        }

        var vectors = await BeirHarness.EmbedAsync(
            generator, embeddings, hypotheticals, cancellationToken);
        var hyde = ToChunkHits(
            await store.SearchAsync(BuildSearchVector(vectors), options, cancellationToken));

        QueryCount++;
        if (!SameRanking(dense, hyde))
        {
            DivergedQueryCount++;
        }

        return hyde;
    }

    /// <summary>
    /// Asserts the hypothetical vector actually moved rankings, before this row's number is read
    /// as a measurement of HyDE retrieval.
    /// </summary>
    /// <param name="datasetName">Names the run in the failure message.</param>
    /// <remarks>
    /// Three checks, in diagnostic order, the shape of
    /// <see cref="RerankedAblationRow.AssertRerankerReordered"/>: whether there is evidence at
    /// all, whether anything moved, then whether enough moved. The fraction is a tenth — mild on
    /// purpose. ArguAna's hypotheticals are compressed <i>restatements</i> of the input argument
    /// (observed during generation, not inferred), so its HyDE vectors legitimately land close to
    /// the query vectors and its rankings legitimately diverge less than FiQA's; a threshold tuned
    /// to FiQA would fail ArguAna for behaving exactly as designed. What the tenth still catches
    /// is the degenerate case, which is not "close": a fallback to the query text produces the
    /// bit-identical query vector, deterministically identical rankings, and a divergence of
    /// exactly zero — an order of magnitude below the bar, on every dataset.
    /// </remarks>
    public void AssertHydeDiverged(string datasetName)
    {
        Assert.True(QueryCount > 0, FormattableString.Invariant($"""
            THE ROW RETRIEVED NOTHING. {datasetName}: {nameof(AssertHydeDiverged)} was called on a
            row that has answered no queries, so there is no evidence to judge. Call it after the
            measurement, on the same row instance the measurement used.
            """));

        Assert.True(DivergedQueryCount > 0, FormattableString.Invariant($"""
            HYDE CHANGED NOTHING. {datasetName}: the HyDE ranking is identical to the dense ranking
            on {DivergedQueryCount} of {QueryCount} queries — every single query, which is what a
            silent fallback to the query text or a mean collapsed onto the query vector looks like:
            the search vector IS the dense vector, so this row's number IS the dense row's number
            and the HyDE label on it is a lie. Check that the hypotheticals are actually read from
            the cache and that their mean, not the query embedding, is what reaches SearchAsync.
            """));

        Assert.True(
            DivergedQueryCount * 10 >= QueryCount,
            FormattableString.Invariant($"""
                HYDE BARELY MOVED ANYTHING. {datasetName}: the HyDE ranking differs from the dense
                ranking on only {DivergedQueryCount} of {QueryCount} queries. The bar is a tenth,
                set deliberately low because ArguAna's hypotheticals restate the query's own
                argument and legitimately land near the query vector — but two genuinely different
                vectors over corpora this size do not produce bit-identical rankings for over nine
                queries in ten. This looks like the hypothetical path collapsing to the query
                embedding for most queries, and for those queries this row's number is the dense
                row's number under a HyDE label.
                """));
    }

    /// <summary>
    /// Mean-pools the hypothesis vectors and L2-normalises the mean — <c>HydeBehavior</c>'s
    /// <c>AverageAndNormalize</c>, double accumulation for the norm and all — but where the
    /// library falls back to the single-doc text path on degenerate input, a measurement has
    /// nothing to fall back to, so this throws instead.
    /// </summary>
    /// <param name="hypothesisVectors">One vector per hypothesis, as the embedder returned them.</param>
    /// <returns>The unit-length mean vector.</returns>
    /// <exception cref="InvalidOperationException">
    /// The vectors disagree on dimension — two models' vectors reached one mean — or the mean has
    /// zero norm, which searches like a vector but means nothing.
    /// </exception>
    internal static float[] BuildSearchVector(IReadOnlyList<float[]> hypothesisVectors)
    {
        var dimension = hypothesisVectors[0].Length;
        var pooled = new float[dimension];
        for (var i = 0; i < hypothesisVectors.Count; i++)
        {
            var vector = hypothesisVectors[i];
            if (vector.Length != dimension)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Hypothesis vector {i} has dimension {vector.Length} but vector 0 has {dimension}. ") +
                    "One mean over two models' vectors measures nothing; check the embedding " +
                    "cache's model identity.");
            }

            for (var d = 0; d < dimension; d++)
            {
                pooled[d] += vector[d];
            }
        }

        var count = hypothesisVectors.Count;
        double normSquared = 0;
        foreach (ref var value in pooled.AsSpan())
        {
            value /= count;
            normSquared += (double)value * value;
        }

        if (normSquared == 0)
        {
            throw new InvalidOperationException(
                "The mean hypothesis vector has zero norm. It cannot be normalised and would " +
                "search as noise; the hypothesis embeddings themselves are suspect.");
        }

        var norm = (float)Math.Sqrt(normSquared);
        foreach (ref var value in pooled.AsSpan())
        {
            value /= norm;
        }

        return pooled;
    }

    /// <summary>
    /// The generator handed to the refuse-on-miss cache, which never invokes it: a miss throws
    /// first. If this ever runs, the cache's mode contract is broken — say so rather than return.
    /// </summary>
    private static Task<string> RefuseToGenerate(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Unreachable: a refuse-on-miss HypotheticalCache must throw on a miss before invoking " +
            "generation, and this row never opens any other kind.");

    /// <summary>Reports whether the HyDE list carries exactly the dense list's chunks in order.</summary>
    private static bool SameRanking(IReadOnlyList<ChunkHit> dense, IReadOnlyList<ChunkHit> hyde)
    {
        if (dense.Count != hyde.Count)
        {
            return false;
        }

        for (var i = 0; i < dense.Count; i++)
        {
            if (!string.Equals(dense[i].ChunkId, hyde[i].ChunkId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
