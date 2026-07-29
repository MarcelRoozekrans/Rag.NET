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

    /// <summary>
    /// Whether <c>UseQAPairsChunking()</c> also registers <see cref="QAPairsDocumentParser"/> and
    /// its <see cref="Abstractions.ParserClaim"/>. Defaults to <see langword="true"/>; set it to
    /// <see langword="false"/> to take the chunking strategy alone.
    /// </summary>
    /// <remarks>
    /// Nothing ships in this repository that collides with this parser's claims today — after
    /// Phase 3.11 it claims only <c>text/csv</c> and the two spreadsheet types. The flag is here
    /// because the conflict guard fires on <i>any</i> duplicated claim, including one a
    /// third-party CSV parser introduces, and an escape hatch that exists on
    /// <see cref="EmailChunkingOptions.RegisterParser"/> but not on its twin is a trap of its own:
    /// the user who hits the second conflict finds the error naming an opt-out the sibling call
    /// does not have.
    /// </remarks>
    public bool RegisterParser { get; set; } = true;

    internal static readonly string[] DefaultQuestionColumns = ["question", "q", "prompt", "input"];
    internal static readonly string[] DefaultAnswerColumns = ["answer", "a", "response", "output"];
}
