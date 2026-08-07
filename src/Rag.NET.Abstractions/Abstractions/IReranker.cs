using Rag.NET.Models;
using ZeroAlloc.Telemetry;

namespace Rag.NET.Abstractions;

/// <summary>
/// Rescores search results using a cross-encoder model for higher precision ranking.
/// </summary>
/// <remarks>
/// Instrumented by the ZeroAlloc.Telemetry source generator rather than by hand — the pilot
/// described in <c>docs/plans/2026-08-07-telemetry-conversion-assessment-design.md</c>. The
/// emitted <c>RerankerInstrumented</c> proxy opens the span; implementations set no tags of
/// their own. <c>{type}</c> resolves to the wrapped implementation's type name, composed once
/// in the proxy's constructor, which is what makes one <c>[Trace]</c> on a shared interface
/// usable at all: <c>ragnet.rerank.CohereReranker</c> and <c>ragnet.rerank.OnnxReranker</c>
/// rather than one indistinguishable <c>ragnet.rerank</c>.
/// <para>
/// This carries the backend in the span <i>name</i>, where Phase 4.4's convention carried it in
/// a <c>reranker.type</c> <i>tag</i>. That reversal is the substance of the pilot, not an
/// oversight — see the design doc before extending the pattern to the vector stores.
/// </para>
/// </remarks>
[Instrument("Rag.NET", PublicProxy = true)]
public interface IReranker
{
    /// <summary>
    /// Reranks <paramref name="results"/> by computing cross-encoder relevance scores
    /// for each (query, passage) pair.
    /// </summary>
    [Trace("ragnet.rerank.{type}")]
    [TraceTagFromResult("reranker.result.count", "Count")]
    Task<IReadOnlyList<RerankResult>> RerankAsync(
        string query,
        [TraceTag("reranker.candidate.count", "Count")] IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default);
}
