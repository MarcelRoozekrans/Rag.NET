namespace Rag.NET.Chunking.Templates;

public sealed class BookChunkingOptions
{
    public int MaxDepth { get; set; } = 2;

    /// <summary>Patterns that mark a chapter heading, one per level, outermost first.</summary>
    /// <remarks>
    /// <para>
    /// <b>This did not exist until #636, and its absence was the defect.</b> The strategy passed
    /// only <see cref="MaxDepth"/> to the hierarchical merger and relied on whatever heading
    /// structure the parser had already found. <c>TextDocumentParser</c> yields <b>one</b> section
    /// for a whole file, so a plain-text book — the most ordinary form a book arrives in — produced
    /// exactly one chunk. Measured on three verbatim chapters of <i>Alice's Adventures in
    /// Wonderland</i>, whose headings read <c>CHAPTER I.</c>.
    /// </para>
    /// <para>
    /// The defaults match how real books label chapters: the word, optionally followed by a roman
    /// numeral or a number, at the start of a line and tolerant of indentation. Markdown headings
    /// are matched too, so a book that arrives already structured behaves as before.
    /// </para>
    /// <para>
    /// <b>Set this to an empty array to restore the previous behaviour</b> — defer entirely to the
    /// parser's own heading structure — which is the right choice when the document is already
    /// structured and its chapter titles do not use the word "chapter" at all.
    /// </para>
    /// </remarks>
    public string[] HeadingPatterns { get; set; } =
    [
        @"^\s*(?:#\s+|CHAPTER\b|Chapter\b)",
        @"^\s*(?:##\s+|PART\b|Part\b|SECTION\b|Section\b)",
    ];
    public bool IncludeIndex { get; set; } = false;
    public bool IncludeForeword { get; set; } = true;
}
