namespace Rag.NET.Parsers.Pdf.Ocr;

/// <summary>The outcome of one <see cref="IDocumentOcrEngine.RecognizeAsync"/> call.</summary>
/// <param name="PageText">
/// Recognized text keyed by <b>1-based</b> page number — the same numbering PdfPig's
/// <c>Page.Number</c> uses (documented as "the page number (starting at 1)"), so the parser
/// matches a recognized page to the page it parsed without an off-by-one translation. Pages
/// the engine produced no text for may be omitted; the parser keeps its own extraction for
/// those.
/// </param>
/// <param name="BilledPages">
/// The number of pages the provider billed for. Document-level providers price per page of
/// the <i>submitted document</i>, not per page that actually needed OCR, so this is normally
/// the document's full page count even when a single page was scanned. Engines report it to
/// <c>ICostLedger</c>; <see cref="PdfDocumentParser"/> stays unaware of billing.
/// </param>
/// <remarks>
/// Record equality compares <paramref name="PageText"/> by reference, as it does for any
/// reference-typed member that is not itself a record — two results with equal contents but
/// distinct dictionaries are not equal.
/// </remarks>
public sealed record DocumentOcrResult(IReadOnlyDictionary<int, string> PageText, int BilledPages);
