namespace Rag.NET.Chunking.Templates;

public sealed class QAPairsChunkingOptions
{
    public string? QuestionColumn { get; set; }
    public string? AnswerColumn { get; set; }

    internal static readonly string[] DefaultQuestionColumns = ["question", "q", "prompt", "input"];
    internal static readonly string[] DefaultAnswerColumns = ["answer", "a", "response", "output"];
}
