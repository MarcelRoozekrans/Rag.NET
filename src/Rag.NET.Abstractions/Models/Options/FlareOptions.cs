using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;

namespace Rag.NET.Models.Options;

/// <summary>
/// Options for the FLARE (Forward-Looking Active Retrieval) answer engine.
/// Validated in <c>UseFlare</c> at registration time.
/// </summary>
public sealed class FlareOptions
{
    /// <summary>
    /// Sentences scoring below this confidence (0..1) trigger a lookahead retrieval.
    /// Default <c>0.6</c>.
    /// </summary>
    public double ConfidenceThreshold { get; set; } = 0.6;

    /// <summary>
    /// Hard cap on mid-generation retrieval <b>attempts</b> per <c>AskAsync</c> call —
    /// failed attempts count against the budget too (intentional: a failing retriever
    /// should not be retried indefinitely). Once exhausted, further low-confidence
    /// sentences are kept as-is. Default <c>3</c>.
    /// </summary>
    public int MaxRetrievals { get; set; } = 3;

    /// <summary>
    /// Maximum number of sentences generated before the engine stops. Default <c>15</c>.
    /// </summary>
    public int MaxSentences { get; set; } = 15;

    /// <summary>
    /// <c>TopK</c> for mid-generation lookahead retrievals. Default <c>3</c>.
    /// Ignored when <see cref="LookaheadRetrievalOptions"/> is set.
    /// </summary>
    public int LookaheadTopK { get; set; } = 3;

    /// <summary>
    /// Full override for the <see cref="RetrievalOptions"/> used by lookahead retrievals.
    /// When <see langword="null"/> (default), lookaheads run a plain retrieval:
    /// <c>TopK</c> = <see cref="LookaheadTopK"/> with HyDE and multi-query expansion
    /// disabled — the lookahead query is already a synthetic document (query + draft
    /// sentence), so expanding it again multiplies hidden LLM calls for little gain.
    /// When set, the options are used verbatim — including <c>TopK</c>, which the
    /// caller must set explicitly (<see cref="LookaheadTopK"/> is not applied).
    /// </summary>
    public RetrievalOptions? LookaheadRetrievalOptions { get; set; }

    /// <summary>
    /// Optional chat client override for the engine and the default self-assessment scorer.
    /// <see langword="null"/> resolves <c>IChatClient</c> from DI.
    /// </summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>
    /// Optional confidence scorer override. <see langword="null"/> uses the
    /// self-assessment default (one small LLM call per sentence). A logprob-based
    /// scorer is a documented extension point.
    /// </summary>
    public IConfidenceScorer? Scorer { get; set; }
}
