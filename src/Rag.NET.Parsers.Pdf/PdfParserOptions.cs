namespace Rag.NET.Parsers.Pdf;

/// <summary>Options controlling <see cref="PdfDocumentParser"/> table extraction and OCR fallback.</summary>
public sealed class PdfParserOptions
{
    /// <summary>Detect tables from word geometry and emit them as Markdown table sections. Default true.</summary>
    public bool ExtractTables { get; set; } = true;

    /// <summary>Minimum number of vertically adjacent column-aligned rows required to detect a table.</summary>
    public int MinTableRows { get; set; } = 3;

    /// <summary>Minimum number of columns required to detect a table.</summary>
    public int MinTableColumns { get; set; } = 2;

    /// <summary>
    /// Run the per-image <b>Tesseract</b> fallback over a page's embedded images when its
    /// extracted text is shorter than <see cref="OcrMinCharacters"/>. Default false.
    /// <para>
    /// This is the Tesseract switch specifically, and it still requires the package to be
    /// compiled with <c>&lt;EnableOcr&gt;true&lt;/EnableOcr&gt;</c> — the gate exists for
    /// Tesseract's native binaries and out-of-band traineddata.
    /// </para>
    /// <para>
    /// The document-level path ignores this flag entirely: registering an
    /// <see cref="Ocr.IDocumentOcrEngine"/> is its own opt-in, needs no compile gate, and
    /// setting both is a registration-time error rather than a silent precedence rule. The
    /// Tesseract-only settings below (<see cref="TessDataPath"/>, <see cref="OcrLanguage"/>)
    /// are likewise only validated when this flag is set.
    /// </para>
    /// </summary>
    public bool UseOcrFallback { get; set; } = false;

    /// <summary>
    /// Per-page extracted-text length below which OCR triggers. Shared by both engines: it
    /// selects the pages Tesseract recognizes, and it decides whether a document-level engine
    /// is called at all.
    /// </summary>
    public int OcrMinCharacters { get; set; } = 50;

    /// <summary>
    /// Upper bound on the page count of a document that may be handed to a document-level
    /// OCR engine (<see cref="Ocr.IDocumentOcrEngine"/>). A document with more pages than
    /// this skips OCR entirely and logs a warning naming both numbers; its pages are parsed
    /// as plain text exactly as they would be with no engine configured.
    /// <para>
    /// The cap exists because document-level providers bill every page of the <i>submitted
    /// document</i>, not just the pages that needed OCR — a 500-page PDF containing one
    /// scanned page costs 500 pages. Splitting out only the pages that need it would mean
    /// writing PDFs, a dependency this repo does not have.
    /// </para>
    /// <para>
    /// Default 200: generous enough that the documents people actually ingest — reports,
    /// papers, contracts, slide exports — are never silently downgraded, while capping the
    /// worst case a single document can cost at a tenth of Azure Document Intelligence's
    /// 2,000-page per-document service limit. Raise it deliberately, with the provider's
    /// per-page price in view: this is what bounds spend by configuration rather than by
    /// whatever a user happens to ingest.
    /// </para>
    /// <para>
    /// Has no effect on the per-image Tesseract fallback, which runs locally and free.
    /// </para>
    /// </summary>
    public int MaxOcrPages { get; set; } = 200;

    /// <summary>Path to the Tesseract tessdata directory used by the OCR fallback.</summary>
    public string TessDataPath { get; set; } = "./tessdata";

    /// <summary>Tesseract language code used by the OCR fallback.</summary>
    public string OcrLanguage { get; set; } = "eng";
}
