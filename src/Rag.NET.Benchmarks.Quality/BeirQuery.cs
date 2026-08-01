namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// One query from a BEIR <c>queries.jsonl</c> line.
/// </summary>
/// <param name="Id">
/// The <c>_id</c> field, which qrels key on. Most of these are NOT judged in any one split:
/// SciFact ships 1,109 queries and its test qrels judge 300 of them, so the file is a superset of
/// what a run may score. <see cref="IrMetrics.Evaluate"/> enforces that, and the BEIR harness
/// retrieves only for the judged subset — an unjudged query's retrieval cannot be scored.
/// </param>
/// <param name="Text">The query text — a scientific claim, for SciFact.</param>
public sealed record BeirQuery(string Id, string Text);
