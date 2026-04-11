namespace Rag.NET.Evaluation;

public sealed class EvaluationDatasetBuilderOptions
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
}
