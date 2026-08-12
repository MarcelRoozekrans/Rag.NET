namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// The four totals a MultiHop-RAG conversion must produce, asserted against what it actually wrote.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the failure mode of a converter is not a crash. It is writing <i>less</i> —
/// a dropped evidence row, a truncated input, a query shape the reader did not recognise — and
/// still producing a directory that loads, scores and yields a number nobody can tell is wrong. The
/// totals are therefore measured from the published files once, carried as data, and checked before
/// anything is published.
/// </para>
/// <para>
/// Passed in rather than hard-coded inside the conversion so the conversion can be exercised on
/// small synthetic fixtures. <see cref="MultiHopRagSource.PublishedCounts"/> is the real one.
/// </para>
/// </remarks>
/// <param name="DocumentCount">Lines in <c>corpus.jsonl</c>, one per article.</param>
/// <param name="QueryCount">
/// Lines in <c>queries.jsonl</c> — <b>every</b> query in the published file, including the ones
/// nothing judges.
/// </param>
/// <param name="JudgedQueryCount">
/// Distinct query ids in <c>qrels/test.tsv</c>. Smaller than <paramref name="QueryCount"/>, exactly
/// as it is for SciFact, whose 1,109 queries carry 300 judged ones.
/// </param>
/// <param name="JudgementCount">
/// Rows in <c>qrels/test.tsv</c> below the header: distinct (query, document) pairs, since a query
/// citing two facts from one article is still one binary judgement.
/// </param>
public sealed record MultiHopRagCounts(
    int DocumentCount,
    int QueryCount,
    int JudgedQueryCount,
    int JudgementCount);
