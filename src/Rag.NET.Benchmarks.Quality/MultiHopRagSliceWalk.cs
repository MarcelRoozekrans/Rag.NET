namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The derivation behind the GraphRAG guard's sixty-article slice: judged queries in id order,
/// accumulating each one's evidence documents until sixty distinct articles have been seen.
/// <para>
/// <b>Choosing by query is the whole point.</b> A MultiHop-RAG question cites two to four articles
/// <i>because those articles share entities</i> — the same person, company or event reported by two
/// outlets — so a query-derived slice guarantees the cross-article entity recurrence community
/// detection has to find something in. Sixty articles taken off the top of <c>corpus.jsonl</c>
/// might share nothing, community detection would return sixty islands, and global search would
/// pass while finding nothing.
/// </para>
/// <para>
/// <b>The query that crosses the target is taken whole</b>, which is what makes the result
/// self-contained: every accepted query's evidence is inside the slice, so the guard's ground-truth
/// assertion cannot fail for want of a document that was never indexed.
/// </para>
/// <para>
/// <b>This is not what the guard runs on.</b> The guard runs on a pinned literal list, so the slice
/// cannot drift when upstream does. This lives here so that the pin can be <i>checked</i> against a
/// re-derivation, and so the extraction generation tool — which is a console application and cannot
/// see the test project's literals — fills the cache for exactly the articles the guard will ask
/// for. Two implementations of the walk would eventually disagree, and the symptom would be a
/// refuse-on-miss failure on the guard's first chunk that reads like an empty cache.
/// </para>
/// </summary>
public static class MultiHopRagSliceWalk
{
    /// <summary>
    /// How many distinct articles the walk stops at: sixty, the design's "roughly sixty".
    /// </summary>
    /// <remarks>
    /// Big enough that entities have somewhere to recur — sixty news articles from a dozen outlets
    /// over one quarter share people and companies heavily — and small enough that extracting every
    /// chunk of every one of them is a bill in cents rather than in tens of dollars. The walk
    /// happens to land on exactly sixty rather than overshooting, because the query that completes
    /// the set contributes its last article as its last evidence row.
    /// </remarks>
    public const int TargetDocumentCount = 60;

    /// <summary>Derives the slice from a loaded MultiHop-RAG dataset.</summary>
    /// <param name="dataset">The converted dataset, with its test qrels loaded.</param>
    /// <returns>
    /// The queries consumed, in id order, and the distinct articles they cite, in the order the
    /// walk first reached them.
    /// </returns>
    /// <remarks>
    /// Document order within one query comes from a dictionary's enumeration and is therefore not
    /// a promise; callers comparing against a pinned list should compare the articles as a set. The
    /// query sequence is ordered and reproducible, because the ids are sorted here explicitly.
    /// </remarks>
    public static (IReadOnlyList<string> QueryIds, IReadOnlyList<string> DocumentIds) Derive(
        BeirDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var queryIds = SortedJudgedQueryIds(dataset);
        var takenQueries = new List<string>();
        var takenDocuments = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < queryIds.Count; i++)
        {
            var queryId = queryIds[i];
            takenQueries.Add(queryId);
            foreach (var (documentId, grade) in dataset.Qrels[queryId])
            {
                if (grade > 0 && seen.Add(documentId))
                {
                    takenDocuments.Add(documentId);
                }
            }

            if (seen.Count >= TargetDocumentCount)
            {
                break;
            }
        }

        return (takenQueries, takenDocuments);
    }

    /// <summary>Every judged query id, ordinally sorted — which for zero-padded ids is file order.</summary>
    private static List<string> SortedJudgedQueryIds(BeirDataset dataset)
    {
        var queryIds = new List<string>(dataset.Qrels.Count);
        foreach (var queryId in dataset.Qrels.Keys)
        {
            queryIds.Add(queryId);
        }

        queryIds.Sort(StringComparer.Ordinal);
        return queryIds;
    }
}
