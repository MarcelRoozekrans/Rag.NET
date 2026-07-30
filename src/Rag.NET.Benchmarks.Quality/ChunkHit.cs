namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// One retrieved chunk together with the document it was chunked from.
/// </summary>
/// <param name="ChunkId">
/// The chunk's own identifier. Carried for diagnostics only — aggregation never reads it — but
/// carried, because "which chunk of which document scored this" is the first question asked of a
/// parity number that comes out wrong.
/// </param>
/// <param name="DocumentId">
/// The parent document's id, which must be the id BEIR's qrels use. Chunk ids reaching the metrics
/// is the failure <see cref="DocumentRanking"/> exists to prevent, and it does not throw: it scores
/// zero for every query, because no chunk id will ever match a qrels entry.
/// </param>
/// <param name="Score">
/// The retrieval score, higher is better. Cosine similarity over L2-normalised vectors, in this
/// harness.
/// </param>
public sealed record ChunkHit(string ChunkId, string DocumentId, double Score);
