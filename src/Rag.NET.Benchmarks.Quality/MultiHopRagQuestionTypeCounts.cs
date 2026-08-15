namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// How many MultiHop-RAG queries each of the paper's four types holds — pinned by
/// <see cref="MultiHopRagSource.PublishedQuestionTypeCounts"/> and asserted by the conversion, so
/// a per-type accuracy is a mean over the denominator the paper reports and not over whatever a
/// short conversion happened to write.
/// </summary>
/// <param name="Inference">Queries whose answer is an entity — 816 at the pinned revision.</param>
/// <param name="Comparison">Queries whose answer is yes or no — 856.</param>
/// <param name="Temporal">Queries whose answer is yes/no or before/after — 583.</param>
/// <param name="Null">Queries answered "Insufficient information." and judged by nothing — 301.</param>
public sealed record MultiHopRagQuestionTypeCounts(int Inference, int Comparison, int Temporal, int Null);
