namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// A <see cref="MemoryStream"/> that remembers whether anyone disposed it. The seam's contract
/// says the parser owns the stream, so an engine disposing it would break every caller that
/// reads the PDF again afterwards — and nothing else would notice.
/// </summary>
internal sealed class DisposeTrackingStream(byte[] buffer) : MemoryStream(buffer, writable: false)
{
    public bool WasDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}
