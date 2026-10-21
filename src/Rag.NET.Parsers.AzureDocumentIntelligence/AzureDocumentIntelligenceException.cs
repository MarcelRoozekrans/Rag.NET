namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// Thrown when Azure AI Document Intelligence fails to analyze a document for
/// <see cref="AzureDocumentIntelligenceDocumentParser"/>: throttling, an authentication failure,
/// unsupported content or a failed analyze operation. Also thrown, before any request, for a
/// document larger than <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not swallow, and that is deliberate.</b> Unlike the PDF OCR engine, which can fall
/// back to PdfPig's text, a TIFF or HEIF parser has no local text to fall back to. Returning no
/// sections would ingest the document as empty: retrievable, indistinguishable from a blank scan,
/// and silently wrong. <c>PipelineIngestor</c> already isolates the blast radius by catching per
/// document.
/// </para>
/// <para>
/// <b>Only service failures are wrapped.</b> An <see cref="OperationCanceledException"/> from the
/// caller's token propagates untouched, because cancellation is not a failure. The size-cap
/// failure wraps nothing: it is raised locally, so <see cref="Exception.InnerException"/> is
/// <see langword="null"/>.
/// </para>
/// </remarks>
/// <param name="fileName">The document being analyzed when the call failed.</param>
/// <param name="message">What failed, naming the file.</param>
/// <param name="innerException">The service exception, preserved for diagnosis.</param>
public sealed class AzureDocumentIntelligenceException(
    string fileName, string message, Exception? innerException)
    : Exception(message, innerException)
{
    /// <summary>Creates an exception for a failure raised locally, with no service exception to wrap.</summary>
    /// <param name="fileName">The document that was refused.</param>
    /// <param name="message">What failed, naming the file.</param>
    public AzureDocumentIntelligenceException(string fileName, string message)
        : this(fileName, message, innerException: null)
    {
    }

    /// <summary>
    /// The document being analyzed when the call failed. Carried separately from the message so a
    /// handler can act on it (retry that file, quarantine it, record it) without parsing prose.
    /// </summary>
    public string FileName { get; } = fileName;
}
