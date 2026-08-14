namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Which MultiHop-RAG articles the extraction generation tool runs over.
/// <para>
/// <b>An explicit mode rather than a number, because the two corpora answer different questions and
/// cost different amounts.</b> The slice is the GraphRAG guard's fixture — sixty articles, already
/// generated, pinned as a literal in the test project — and it is the default, so an invocation
/// that says nothing does what the tool has always done. The full corpus is the comparative run's
/// input: 609 articles, roughly 41,000 requests, hours of wall clock and real money. Nothing should
/// be able to reach the second by mistyping the first.
/// </para>
/// </summary>
public enum GraphExtractionCorpus
{
    /// <summary>
    /// The pinned sixty-article slice <c>MultiHopRagSliceWalk</c> derives — the default.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="MultiHopRagSliceWalk.TargetDocumentCount"/>: a walk that produced any
    /// other count is refused rather than extracted, because filling the cache for a different
    /// sixty than the guard reads would cost real money and still miss on every key.
    /// </remarks>
    Slice = 0,

    /// <summary>Every article in the converted corpus — all 609 of them.</summary>
    /// <remarks>
    /// The slice is a subset of this, chunked identically, so a full run re-uses every extraction
    /// the slice already paid for: chunking is a function of one document's own
    /// <see cref="BeirDocument.RetrievalText"/>, and the cache is keyed on the rendered prompt.
    /// </remarks>
    Full = 1,
}
