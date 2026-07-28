namespace Rag.NET.Evaluation.Ragas;

/// <summary>Tuning for a RAGAS evaluation run.</summary>
/// <remarks>
/// The concurrency ceiling and the chat token prices come from <see cref="EvaluationCallOptions"/>,
/// which the dataset builder's options also extend. Property names are unchanged, so setting
/// <c>MaxConcurrentCalls</c> or any of the prices on a <see cref="RagasOptions"/> reads exactly as
/// it did before the move.
/// <para>
/// A RAGAS ceiling is shared across every metric in a suite, not per metric: four registered
/// metrics each fanning out over a 50-chunk sample at a ceiling of 2 would be 8 requests in
/// flight, not 2, and the number a caller sets would then not be the number they get.
/// </para>
/// <para>
/// Answer Relevance is the only RAGAS metric that embeds anything, so
/// <see cref="PricePerEmbeddingToken"/> prices that metric's calls alone; the rest of a suite is
/// chat spend priced by the input and output token rates.
/// </para>
/// </remarks>
public sealed class RagasOptions : EvaluationCallOptions
{
    /// <summary>
    /// Number of synthetic questions Answer Relevance generates. Defaults to <c>3</c>.
    /// </summary>
    public int SyntheticQuestionCount { get; set; } = 3;

    /// <summary>
    /// Price of one embedding token, in whatever currency the ledger is denominated in.
    /// Defaults to <c>0</c> — set it from your own price sheet, or cost entries record zero.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EvaluationCallOptions.PricePerInputToken"/> because embeddings are
    /// billed at their own rate, usually an order of magnitude below chat. Embedding APIs bill on
    /// input tokens alone, so this is the only embedding price there is. The ledger never prices
    /// anything itself; the caller computes the cost.
    /// <para>
    /// It lives on <see cref="RagasOptions"/> rather than on <see cref="EvaluationCallOptions"/>
    /// because Answer Relevance is its only consumer. Held on the shared base it was inherited by
    /// <c>EvaluationDatasetBuilderOptions</c>, where a caller could set it and nothing would ever
    /// read it — a dataset build generates text and never embeds.
    /// </para>
    /// </remarks>
    public decimal PricePerEmbeddingToken { get; set; }
}
