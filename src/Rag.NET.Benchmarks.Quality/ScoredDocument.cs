namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// One document and its pooled score, as produced by <see cref="DocumentRanking"/>.
/// </summary>
/// <param name="DocumentId">The document id, matching the ids BEIR's qrels use.</param>
/// <param name="Score">
/// The <b>maximum</b> score over the document's retrieved chunks — not their sum, which would
/// reward long documents for being chunked into more pieces, and not their mean, which would punish
/// a document for containing one strongly matching passage among many unrelated ones.
/// </param>
public sealed record ScoredDocument(string DocumentId, double Score);
