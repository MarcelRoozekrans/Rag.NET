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
    /// Run OCR over a page's embedded images when its extracted text is shorter than
    /// <see cref="OcrMinCharacters"/>. Requires the package to be compiled with
    /// <c>&lt;EnableOcr&gt;true&lt;/EnableOcr&gt;</c>. Default false.
    /// </summary>
    public bool UseOcrFallback { get; set; } = false;

    /// <summary>Per-page extracted-text length below which the OCR fallback triggers.</summary>
    public int OcrMinCharacters { get; set; } = 50;

    /// <summary>Path to the Tesseract tessdata directory used by the OCR fallback.</summary>
    public string TessDataPath { get; set; } = "./tessdata";

    /// <summary>Tesseract language code used by the OCR fallback.</summary>
    public string OcrLanguage { get; set; } = "eng";
}
