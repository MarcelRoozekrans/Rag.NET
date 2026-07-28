namespace Rag.NET.Evaluation;

/// <summary>Tuning shared by everything in this library that talks to an LLM.</summary>
/// <remarks>
/// <para>
/// The concurrency ceiling and the chat token prices are identical concerns for a RAGAS run and for
/// a dataset build, and both are consumed by the same shared caller. They live here so there is one
/// definition rather than one per feature — the same reasoning that gave the throttle-and-cost
/// plumbing a single home.
/// </para>
/// <para>
/// Only what every derived option actually consumes belongs here. An embedding price does not:
/// Answer Relevance is the one thing in either library that embeds, so
/// <c>RagasOptions.PricePerEmbeddingToken</c> lives on that run's options rather than on this base,
/// where a dataset build would inherit a knob it can never act on.
/// </para>
/// </remarks>
public abstract class EvaluationCallOptions
{
    /// <summary>
    /// Maximum LLM calls in flight across the whole run. Defaults to <c>4</c>.
    /// </summary>
    /// <remarks>
    /// Shared across the whole run rather than per component. Per-component ceilings multiply: four
    /// registered metrics each fanning out over a 50-chunk sample is 200 concurrent requests,
    /// which is how the pre-3.1 code behaved with no ceiling at all.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 4;

    /// <summary>
    /// Price of one input token, in whatever currency the ledger is denominated in.
    /// Defaults to <c>0</c> — set it from your own price sheet, or cost entries record zero.
    /// </summary>
    /// <remarks>The ledger never prices anything itself; the caller computes the cost.</remarks>
    public decimal PricePerInputToken { get; set; }

    /// <summary>Price of one output token. Defaults to <c>0</c>. See <see cref="PricePerInputToken"/>.</summary>
    public decimal PricePerOutputToken { get; set; }
}
