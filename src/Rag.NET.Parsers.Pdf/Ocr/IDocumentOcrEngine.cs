namespace Rag.NET.Parsers.Pdf.Ocr;

/// <summary>
/// Seam for OCR engines that accept a <b>whole PDF</b> and return every page from a single
/// call — the shape cloud document-understanding services expose, where the service
/// rasterizes server-side and prices per page of the submitted document.
/// <para>
/// It is the sibling of <c>IPdfOcrEngine</c>, which recognizes one embedded image at a time
/// for an in-process native library. The two seams never interact:
/// <see cref="PdfDocumentParser"/> uses whichever one is configured, and configuring both is
/// a registration-time error rather than a silent precedence rule.
/// </para>
/// <para>
/// <b>Public on purpose.</b> Implementations ship in separate assemblies, so this is a
/// deliberate public API commitment rather than an <c>InternalsVisibleTo</c> back door.
/// </para>
/// <para>
/// <b>Asynchronous and cancellable from the outset.</b> <c>IPdfOcrEngine</c> is synchronous
/// and takes no <see cref="CancellationToken"/> because it was designed around a local
/// native library — and that is precisely the mistake this seam exists not to repeat. A
/// network-backed engine forced through a synchronous, token-less signature would mean
/// sync-over-async inside <see cref="PdfDocumentParser.ParseAsync"/> and no way to honour
/// the caller's cancellation while a long-running operation is polled.
/// </para>
/// </summary>
public interface IDocumentOcrEngine
{
    /// <summary>
    /// Recognizes the text of every page of <paramref name="pdf"/> in one call.
    /// </summary>
    /// <param name="pdf">
    /// The complete PDF, positioned at its first byte. The parser owns this stream and
    /// disposes it after the call returns; implementations must not dispose it, and must not
    /// retain it beyond the call.
    /// </param>
    /// <param name="cancellationToken">
    /// The caller's token. Implementations must honour it throughout, including while
    /// polling a long-running operation.
    /// </param>
    /// <returns>
    /// Per-page text keyed by 1-based page number, plus the page count the provider billed.
    /// </returns>
    /// <remarks>
    /// Failures may surface as exceptions: the parser treats them as a degradation, logs a
    /// warning and falls back to its own extraction losslessly. Cancellation is not a
    /// failure — an <see cref="OperationCanceledException"/> propagates to the caller.
    /// </remarks>
    ValueTask<DocumentOcrResult> RecognizeAsync(Stream pdf, CancellationToken cancellationToken);
}
