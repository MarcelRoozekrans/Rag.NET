using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Scores how confident the system is that a draft answer sentence is correct and supported
/// by the retrieved context. Used by FLARE-style engines to decide when to trigger a
/// mid-generation lookahead retrieval.
/// </summary>
public interface IConfidenceScorer
{
    /// <summary>
    /// Returns a confidence score in the range <c>0..1</c> — the probability that
    /// <paramref name="sentence"/> is correct and supported by <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Implementations should fail <b>open</b>: when scoring cannot be performed
    /// (LLM failure, unparsable output), return <c>1.0</c> so callers do not trigger
    /// spurious re-retrievals. Cancellation must propagate.
    /// </remarks>
    /// <param name="sentence">The draft sentence to assess.</param>
    /// <param name="partialAnswer">The answer generated so far (excluding <paramref name="sentence"/>).</param>
    /// <param name="context">The retrieval context the sentence should be supported by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<double> ScoreAsync(
        string sentence,
        string partialAnswer,
        IReadOnlyList<SearchResult> context,
        CancellationToken cancellationToken = default);
}
