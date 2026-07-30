namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// One BEIR dataset split as loaded from disk: the corpus, every query, and the relevance
/// judgements for one split.
/// </summary>
/// <param name="Name">The BEIR dataset name, for example <c>scifact</c>.</param>
/// <param name="Split">The qrels split these judgements came from, for example <c>test</c>.</param>
/// <param name="Documents">Every corpus document, in file order.</param>
/// <param name="Queries">
/// Every query in <c>queries.jsonl</c> — a superset of the judged ones. SciFact ships 1,109 here
/// against 300 in <paramref name="Qrels"/>.
/// </param>
/// <param name="Qrels">
/// Relevance judgements for <paramref name="Split"/>, by query id then document id, in exactly the
/// shape <see cref="IrMetrics.Evaluate"/> takes.
/// </param>
public sealed record BeirDataset(
    string Name,
    string Split,
    IReadOnlyList<BeirDocument> Documents,
    IReadOnlyList<BeirQuery> Queries,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Qrels)
{
    /// <summary>
    /// Gets how many queries the split judges — 300 for SciFact's test split.
    /// </summary>
    /// <remarks>
    /// Worth asserting against before reporting a number. The gap between this and
    /// <c>Queries.Count</c> is the whole of the dilution trap: evaluating all 1,109 SciFact queries
    /// divides the mean by roughly 3.7 and turns 0.645 into about 0.17.
    /// </remarks>
    public int JudgedQueryCount => Qrels.Count;
}
