using Rag.NET.Ingestion;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

/// <summary>
/// Which content type a document is parsed as (issue #130).
/// <para>
/// The case that went unnoticed is <see cref="PdfWithNoDeclaredType_IsInferredFromTheFilename"/>.
/// A <c>FileEntry</c> with no <c>ContentType</c>, ingested through a provider with no
/// <c>baseMetadata</c>, resolved to <c>text/plain</c>; <c>TextDocumentParser</c> claims that, so
/// PDF bytes were read as text and the garbage was chunked, embedded and stored — with no
/// exception and no warning. Nothing pinned this path, which is why it survived.
/// </para>
/// </summary>
public class DocumentContentTypeResolverTests
{
    [Fact]
    public void ADeclaredTypeWinsOverTheFilename()
    {
        // The caller knows better than the extension — a .txt holding JSON, say.
        var resolved = DocumentContentTypeResolver.Resolve(
            Metadata(fileName: "data.txt", contentType: "application/json"));

        Assert.Equal("application/json", resolved);
    }

    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("REPORT.PDF", "application/pdf")]
    [InlineData("notes.md", "text/markdown")]
    public void PdfWithNoDeclaredType_IsInferredFromTheFilename(string fileName, string expected)
    {
        var resolved = DocumentContentTypeResolver.Resolve(Metadata(fileName, contentType: null));

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("server.log")]
    [InlineData("app.cfg")]
    [InlineData("README")]
    [InlineData("archive.somethingnobodyknows")]
    public void AnUnknownExtensionStaysText(string fileName)
    {
        // ContentTypeMap answers application/octet-stream for a miss, and nothing parses that —
        // so using it would turn working ingestion of genuinely-textual files into a throw. The
        // fix is deliberately narrow: known formats parse correctly, everything else is unchanged.
        var resolved = DocumentContentTypeResolver.Resolve(Metadata(fileName, contentType: null));

        Assert.Equal("text/plain", resolved);
    }

    [Fact]
    public void OctetStreamNeverReachesTheParsers()
    {
        // ContentTypeMap's own remarks depend on this: its fallback assumes no parser claims
        // application/octet-stream, which is what makes an unclaimed container attachment a
        // warn-and-skip rather than a hard failure.
        var resolved = DocumentContentTypeResolver.Resolve(Metadata("mystery.zzz", contentType: null));

        Assert.NotEqual("application/octet-stream", resolved, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankDeclaredTypeIsTreatedAsAbsent(string contentType)
    {
        // Otherwise an empty string from configuration binding would be handed to the parsers as
        // a content type, and nothing claims "".
        var resolved = DocumentContentTypeResolver.Resolve(Metadata("report.pdf", contentType));

        Assert.Equal("application/pdf", resolved);
    }

    private static DocumentMetadata Metadata(string fileName, string? contentType) => new()
    {
        DocumentId = new DocumentId("doc-1"),
        FileName = fileName,
        ContentType = contentType,
    };
}
