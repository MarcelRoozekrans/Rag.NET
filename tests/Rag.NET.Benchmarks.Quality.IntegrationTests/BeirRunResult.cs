using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// One measurement of one dataset under one protocol: the metrics, and enough of the run's shape to
/// tell why two protocols disagreed.
/// </summary>
/// <remarks>
/// The counts are not decoration. Phase 3.12's whole risk is a real-chunking run that reports the
/// same number as the parity run — which looks like a pass and means either the chunker did not
/// chunk or the aggregation did not aggregate. <see cref="IndexedChunkCount"/> and
/// <see cref="PooledQueryCount"/> are the two facts that tell those apart, and neither is visible in
/// an nDCG.
/// </remarks>
/// <param name="Evaluation">The metrics <see cref="IrMetrics.Evaluate"/> produced.</param>
/// <param name="DocumentCount">Corpus documents indexed.</param>
/// <param name="IndexedChunkCount">
/// Text units actually embedded and stored. Equal to <paramref name="DocumentCount"/> under the
/// parity protocol by construction, and strictly greater under any protocol that chunks — so
/// <c>IndexedChunkCount == DocumentCount</c> on a run that was supposed to chunk is "the chunker did
/// not chunk", stated directly rather than inferred from a number that failed to move.
/// </param>
/// <param name="IndexedDocumentCount">
/// Distinct documents that contributed at least one unit. Below <paramref name="DocumentCount"/>
/// means the protocol dropped documents outright, which no amount of nDCG will say: a document that
/// was never indexed is unretrievable, and it looks from the metric exactly like a document that was
/// indexed and ranked badly. FiQA has 38 corpus entries whose title and text are both empty, one of
/// which is judged relevant, and a chunker that yields nothing for empty input loses all 38.
/// </param>
/// <param name="MaxChunksPerDocument">
/// The largest number of units any single document contributed. Sets how far retrieval has to
/// over-shoot the cutoff for pooling to still see <c>k</c> distinct documents.
/// </param>
/// <param name="PooledQueryCount">
/// Queries whose retrieved chunk list held two or more units of the same document — the queries on
/// which max-pooling had anything to do at all. Zero here on a chunked run is "the aggregation did
/// not aggregate", and it is the other half of the finding the plan asks for.
/// </param>
/// <param name="Elapsed">Wall-clock time for the measurement, cache hits included.</param>
/// <param name="CacheHits">Texts served from <see cref="EmbeddingCache"/> rather than embedded.</param>
/// <param name="CacheMisses">Texts that had to be embedded.</param>
public sealed record BeirRunResult(
    IrEvaluation Evaluation,
    int DocumentCount,
    int IndexedChunkCount,
    int IndexedDocumentCount,
    int MaxChunksPerDocument,
    int PooledQueryCount,
    TimeSpan Elapsed,
    long CacheHits,
    long CacheMisses)
{
    /// <summary>Gets the headline metric, the one every band in this project is about.</summary>
    public double NdcgAt10 => Evaluation.NormalizedDiscountedCumulativeGain;

    /// <summary>
    /// Gets whether more units were indexed than there are documents — the direct evidence that the
    /// protocol chunked, independent of whether the score moved.
    /// </summary>
    public bool Chunked => IndexedChunkCount > DocumentCount;

    /// <summary>Gets how many documents contributed no unit at all.</summary>
    public int UnindexedDocumentCount => DocumentCount - IndexedDocumentCount;

    /// <summary>
    /// Gets the run as one line, leading with the metric and following with the shape that explains
    /// it.
    /// </summary>
    /// <returns>A multi-line description for the test output.</returns>
    public string Describe() => FormattableString.Invariant($"""
        nDCG@{Evaluation.Cutoff} = {NdcgAt10:F5}, Recall@{Evaluation.Cutoff} = {Evaluation.Recall:F5}, MRR@{Evaluation.Cutoff} = {Evaluation.MeanReciprocalRank:F5}
        {Evaluation.EvaluatedQueryCount} queries evaluated, {Evaluation.ExcludedQueryCount} excluded as unjudged
        {IndexedChunkCount} units indexed over {IndexedDocumentCount} of {DocumentCount} documents (max {MaxChunksPerDocument} per document, {UnindexedDocumentCount} contributed nothing)
        {PooledQueryCount} queries retrieved two or more units of one document
        embedding cache: {CacheHits} hits, {CacheMisses} misses
        elapsed {Elapsed.TotalSeconds:F1} s
        """);
}
