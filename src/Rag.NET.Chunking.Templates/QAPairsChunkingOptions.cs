namespace Rag.NET.Chunking.Templates;

public sealed class QAPairsChunkingOptions
{
    public string? QuestionColumn { get; set; }
    public string? AnswerColumn { get; set; }

    /// <summary>
    /// Whether the first row of a CSV file is a header row.
    /// Defaults to <c>true</c>. Set to <c>false</c> for header-less CSVs,
    /// in which case <see cref="QuestionColumn"/> and <see cref="AnswerColumn"/>
    /// must be provided as zero-based column indices expressed as strings (e.g. "0", "1").
    /// </summary>
    public bool SkipHeader { get; set; } = true;

    internal static readonly string[] DefaultQuestionColumns = ["question", "q", "prompt", "input"];
    internal static readonly string[] DefaultAnswerColumns = ["answer", "a", "response", "output"];
}
