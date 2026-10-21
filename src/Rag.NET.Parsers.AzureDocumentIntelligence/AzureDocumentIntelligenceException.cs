namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// Thrown when Azure AI Document Intelligence fails to analyze a document for
/// <see cref="AzureDocumentIntelligenceDocumentParser"/>: throttling, an authentication failure,
/// unsupported content or a failed analyze operation.
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
/// caller's token and <c>BudgetExceededException</c> propagate untouched: the first is not a
/// failure, and the second is this library's own stop signal, which must stay recognisable by
/// type so nothing retries past a spend limit.
/// </para>
/// </remarks>
/// <param name="fileName">The document being analyzed when the call failed.</param>
/// <param name="message">What failed, naming the file.</param>
/// <param name="innerException">The service exception, preserved for diagnosis.</param>
public sealed class AzureDocumentIntelligenceException(
    string fileName, string message, Exception innerException)
    : Exception(message, innerException)
{
    /// <summary>
    /// The document being analyzed when the call failed. Carried separately from the message so a
    /// handler can act on it (retry that file, quarantine it, record it) without parsing prose.
    /// </summary>
    public string FileName { get; } = fileName;
}
