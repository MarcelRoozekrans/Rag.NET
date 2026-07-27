namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// The names registered metrics are reported under.
/// </summary>
/// <remarks>
/// These strings are the keys of <see cref="RagasReport.UnscoreableSamples"/> and
/// <see cref="RagasSampleScore.Scores"/>, and they are written by the builder and read by the
/// aggregation. Two literals that must agree across two files is exactly the arrangement that
/// drifts.
/// </remarks>
internal static class RagasMetricNames
{
    /// <summary>The key Faithfulness is reported under.</summary>
    public const string Faithfulness = "Faithfulness";

    /// <summary>The key Answer Relevance is reported under.</summary>
    public const string AnswerRelevance = "AnswerRelevance";

    /// <summary>The key Context Precision is reported under.</summary>
    public const string ContextPrecision = "ContextPrecision";

    /// <summary>The key Context Recall is reported under.</summary>
    public const string ContextRecall = "ContextRecall";
}
