namespace Rag.NET.Evaluation;

/// <summary>
/// The result of a synthetic dataset build: the samples that generated, and an account of the
/// sampled chunks that produced nothing.
/// </summary>
/// <remarks>
/// <see cref="EvaluationDatasetBuilder.BuildAsync"/> returns this rather than a bare
/// <c>IReadOnlyList&lt;EvaluationSample&gt;</c> because a short list on its own is silent about why
/// it is short. Two builds of the same corpus that return 47 samples out of 50 are not the same
/// event if one dropped three generations and the other only found 47 chunks.
/// </remarks>
public sealed class EvaluationDataset
{
    /// <summary>Samples that generated successfully.</summary>
    public IReadOnlyList<EvaluationSample> Samples { get; init; } = [];

    /// <summary>How many chunks were sampled and sent for generation.</summary>
    public int Requested { get; init; }

    /// <summary>
    /// How many sampled chunks produced no usable sample, by reason.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than folded into a shorter list, for the same reason
    /// <c>RagasReport.UnscoreableSamples</c> exists: a caller who asks for 50 and receives 47
    /// should be able to see why, instead of quietly evaluating against a smaller set. The keys are
    /// the <see cref="SkipReasons"/> constants; a reason that never occurred is absent rather than
    /// present with a zero.
    /// </remarks>
    public IReadOnlyDictionary<string, int> Skipped { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The reasons a sampled chunk can fail to become a sample.</summary>
    /// <remarks>
    /// Constants rather than an enum because <see cref="Skipped"/> is keyed by string: a dataset is
    /// something callers serialise and compare across runs, and a name survives that where an
    /// ordinal does not.
    /// </remarks>
    public static class SkipReasons
    {
        /// <summary>The model returned nothing, or only whitespace, for the question.</summary>
        public const string EmptyQuestion = "EmptyQuestion";

        /// <summary>
        /// The model returned nothing, or only whitespace, for the reference answer in
        /// <see cref="DatasetGenerationMode.QuestionAndAnswer"/> mode.
        /// </summary>
        public const string EmptyReferenceAnswer = "EmptyReferenceAnswer";
    }
}
