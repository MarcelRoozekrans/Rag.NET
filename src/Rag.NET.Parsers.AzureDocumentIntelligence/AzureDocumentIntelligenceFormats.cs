namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// The document formats <see cref="AzureDocumentIntelligenceDocumentParser"/> claims. Every
/// claimed document is sent to Azure and billed, so each format is an explicit opt-in.
/// </summary>
/// <remarks>
/// The default is <see cref="Tiff"/> | <see cref="Heif"/>: the formats no local parser in
/// Rag.NET reads. The rest are opt-in because a local parser already handles them for free. For
/// <see cref="Office"/> and <see cref="Html"/> in particular, the service extracts the same
/// embedded text the local parsers do and ignores embedded images, so routing them here adds
/// cost, not text; the only reason to do it is a single pipeline for every format.
/// </remarks>
[Flags]
public enum AzureDocumentIntelligenceFormats
{
    /// <summary>No format; the parser claims nothing.</summary>
    None = 0,

    /// <summary>TIFF, analyzed one page per frame (<c>image/tiff</c>).</summary>
    Tiff = 1 << 0,

    /// <summary>HEIF and HEIC (<c>image/heif</c>, <c>image/heic</c>).</summary>
    Heif = 1 << 1,

    /// <summary>PDF (<c>application/pdf</c>). Otherwise read locally by the PDF parser.</summary>
    Pdf = 1 << 2,

    /// <summary>PNG, JPEG and BMP. Otherwise described by the vision parser.</summary>
    Images = 1 << 3,

    /// <summary>Word <c>.docx</c>. Otherwise read locally by the Office parser.</summary>
    Word = 1 << 4,

    /// <summary>Excel <c>.xlsx</c>. Otherwise read locally by the Office parser.</summary>
    Excel = 1 << 5,

    /// <summary>PowerPoint <c>.pptx</c>. Otherwise read locally by the Office parser.</summary>
    PowerPoint = 1 << 6,

    /// <summary>HTML (<c>text/html</c>). Otherwise read locally by the HTML parser.</summary>
    Html = 1 << 7,

    /// <summary><see cref="Word"/>, <see cref="Excel"/> and <see cref="PowerPoint"/>.</summary>
    Office = Word | Excel | PowerPoint,
}
