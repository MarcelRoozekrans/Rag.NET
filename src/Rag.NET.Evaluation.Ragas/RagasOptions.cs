namespace Rag.NET.Evaluation.Ragas;

/// <summary>Tuning for a RAGAS evaluation run.</summary>
public sealed class RagasOptions
{
    /// <summary>
    /// Maximum LLM calls in flight across the whole run. Defaults to <c>4</c>.
    /// </summary>
    /// <remarks>
    /// Shared across every metric in a suite, not per metric. Per-metric ceilings multiply: four
    /// registered metrics each fanning out over a 50-chunk sample is 200 concurrent requests,
    /// which is how the pre-3.1 code behaved with no ceiling at all.
    /// </remarks>
    public int MaxConcurrentCalls { get; set; } = 4;

    /// <summary>
    /// Number of synthetic questions Answer Relevance generates. Defaults to <c>3</c>.
    /// </summary>
    public int SyntheticQuestionCount { get; set; } = 3;

    /// <summary>
    /// Price of one input token, in whatever currency the ledger is denominated in.
    /// Defaults to <c>0</c> — set it from your own price sheet, or cost entries record zero.
    /// </summary>
    /// <remarks>The ledger never prices anything itself; the caller computes the cost.</remarks>
    public decimal PricePerInputToken { get; set; }

    /// <summary>Price of one output token. Defaults to <c>0</c>. See <see cref="PricePerInputToken"/>.</summary>
    public decimal PricePerOutputToken { get; set; }

    /// <summary>
    /// Price of one embedding token, in whatever currency the ledger is denominated in.
    /// Defaults to <c>0</c> — set it from your own price sheet, or cost entries record zero.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PricePerInputToken"/> because embeddings are billed at their own
    /// rate, usually an order of magnitude below chat. Answer Relevance is the only metric that
    /// embeds anything, and embedding APIs bill on input tokens alone, so this is the only
    /// embedding price there is. The ledger never prices anything itself; the caller computes
    /// the cost.
    /// </remarks>
    public decimal PricePerEmbeddingToken { get; set; }
}
