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
/// The number of pages the provider billed for — the page count of the <b>submitted
/// document</b>, not the number of pages that came back with text. Document-level providers
/// price per submitted page, so a 500-page PDF containing one scanned page bills 500 even
/// though <paramref name="PageText"/> may hold a single entry. Engines report this to
/// <c>ICostLedger</c>; <see cref="PdfDocumentParser"/> stays unaware of billing.
/// </param>
/// <remarks>
/// Record equality compares <paramref name="PageText"/> by reference, as it does for any
/// reference-typed member that is not itself a record — two results with equal contents but
/// distinct dictionaries are not equal.
/// <para>
/// The argument guards below run at construction only. C#'s <c>with</c> expression copies
/// fields directly and does not re-run property initializers, so a copy can carry values the
/// constructor would have rejected. Construct rather than <c>with</c> when the values are
/// untrusted.
/// </para>
/// </remarks>
public sealed record DocumentOcrResult(IReadOnlyDictionary<int, string> PageText, int BilledPages)
{
    /// <inheritdoc cref="DocumentOcrResult(IReadOnlyDictionary{int, string}, int)"/>
    public IReadOnlyDictionary<int, string> PageText { get; init; } =
        PageText ?? throw new ArgumentNullException(nameof(PageText));

    /// <inheritdoc cref="DocumentOcrResult(IReadOnlyDictionary{int, string}, int)"/>
    public int BilledPages { get; init; } = BilledPages >= 0
        ? BilledPages
        : throw new ArgumentOutOfRangeException(
            nameof(BilledPages), BilledPages, "Billed pages must not be negative.");
}
