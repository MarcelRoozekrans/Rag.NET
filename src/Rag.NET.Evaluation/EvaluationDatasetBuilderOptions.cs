namespace Rag.NET.Evaluation;

/// <summary>Tuning for a synthetic dataset build.</summary>
/// <remarks>
/// The concurrency ceiling and the token prices come from <see cref="EvaluationCallOptions"/>,
/// which a RAGAS run's options also extend: a dataset build is LLM spend under a ceiling like any
/// other, and there is one definition of that rather than one per feature.
/// </remarks>
public sealed class EvaluationDatasetBuilderOptions : EvaluationCallOptions
{
    /// <summary>Number of chunks to sample. Clamped to available chunk count.</summary>
    public int SampleCount { get; init; } = 20;

    /// <summary>
    /// <see cref="DatasetGenerationMode.QuestionOnly"/> produces samples with an empty
    /// <see cref="EvaluationSample.ReferenceAnswer"/> — 1 LLM call per chunk.
    /// <see cref="DatasetGenerationMode.QuestionAndAnswer"/> adds a second LLM call to
    /// generate a ground-truth answer — required for Context Precision/Recall metrics.
    /// </summary>
    public DatasetGenerationMode Mode { get; init; } = DatasetGenerationMode.QuestionOnly;

    /// <summary>
    /// Seeds the chunk sampling. Null — the default — samples differently on every build, so the
    /// dataset cannot be regenerated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarantee is exactly this: <b>the same seed over the same corpus samples the same
    /// chunks.</b> That is what makes a before/after comparison mean something — rebuild the
    /// dataset after a chunking change and the delta measures the change rather than a fresh draw
    /// of questions.
    /// </para>
    /// <para>
    /// It does <b>not</b> survive ingestion. The seed fixes which chunks are drawn from what is
    /// there; ingesting or deleting documents changes what is there, and the same seed then draws
    /// a different set. Reproducibility holds for a fixed corpus, not across changes to it.
    /// </para>
    /// <para>
    /// It does <b>not</b> fix the generated text. Question generation is an LLM call and the model
    /// is not seeded by this: above temperature 0 the same chunk yields a different question on
    /// every build. Seeding selects the same chunks, not the same questions.
    /// </para>
    /// <para>
    /// It also depends on <see cref="Rag.NET.Abstractions.IRagDataManager"/> enumerating documents
    /// and chunks in a stable order — a condition on the store, stated rather than assumed.
    /// Reservoir sampling's decision at each item depends on every item before it, so a store that
    /// returns the corpus in a different order selects differently even with the same seed.
    /// </para>
    /// </remarks>
    public int? Seed { get; init; }
}
