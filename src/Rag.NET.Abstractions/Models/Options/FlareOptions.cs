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
    /// Hard cap on mid-generation retrievals per <c>AskAsync</c> call. Once exhausted,
    /// further low-confidence sentences are kept as-is. Default <c>3</c>.
    /// </summary>
    public int MaxRetrievals { get; set; } = 3;

    /// <summary>
    /// Maximum number of sentences generated before the engine stops. Default <c>15</c>.
    /// </summary>
    public int MaxSentences { get; set; } = 15;

    /// <summary>
    /// <c>TopK</c> for mid-generation lookahead retrievals. Default <c>3</c>.
    /// </summary>
    public int LookaheadTopK { get; set; } = 3;

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
