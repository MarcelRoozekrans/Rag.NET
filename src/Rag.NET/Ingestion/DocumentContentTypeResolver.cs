using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Ingestion;

/// <summary>
/// Decides which content type a document is parsed as.
/// <para>
/// <b>One resolver because two call sites disagreed.</b> <c>ParseBehavior</c> and
/// <c>ParentDocumentIngestionBehavior</c> each wrote <c>ContentType ?? "text/plain"</c> and then
/// selected a parser differently — <c>FirstOrDefault</c> with an explicit throw against
/// <c>First</c> — so the same unparseable document surfaced as
/// <see cref="NoParserFoundException"/> on one path and a bare
/// <see cref="InvalidOperationException"/> on the other, and only the first is catchable as the
/// documented <c>RagError.NoParserFound</c> (issue #130).
/// </para>
/// <para>
/// <b>The filename is consulted, which it never used to be.</b> <c>FileEntry.FileName</c> is
/// documented as being "used for MIME/parser detection", and no core path read it for that. A
/// provider yielding a PDF with no content type fell straight through to <c>text/plain</c>,
/// <c>TextDocumentParser</c> claimed it because it claims everything textual, and PDF bytes were
/// read as text, chunked, embedded and stored. No exception, no warning — the quietest of the
/// three available outcomes.
/// </para>
/// </summary>
internal static class DocumentContentTypeResolver
{
    /// <summary>The type assumed when neither the caller nor the filename says otherwise.</summary>
    private const string DefaultContentType = "text/plain";

    /// <summary>
    /// The content type to parse <paramref name="metadata"/>'s document as: the declared type,
    /// then the filename's extension, then <see cref="DefaultContentType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An extension the map does not know falls back to text rather than failing.</b>
    /// <see cref="ContentTypeMap.FromFileName"/> answers <c>application/octet-stream</c> for a
    /// miss, and feeding that to the parsers would throw, since nothing claims it. That is a
    /// louder failure and the wrong one: an unknown extension is most often a <c>.log</c>, a
    /// <c>.cfg</c>, or a file with no extension at all, and those really are text. Refusing them
    /// would break working ingestion to fix a case that is not broken.
    /// </para>
    /// <para>
    /// It also keeps <c>application/octet-stream</c> out of the ingestion path entirely, which
    /// <see cref="ContentTypeMap"/>'s own remarks depend on: its fallback assumes no parser claims
    /// that type, and that invariant is what makes an unclaimed container attachment a
    /// warn-and-skip rather than a hard failure.
    /// </para>
    /// <para>
    /// So the change is narrow on purpose. A format the map recognises — <c>.pdf</c>,
    /// <c>.docx</c>, <c>.xlsx</c> — now parses correctly instead of silently as text; everything
    /// else behaves exactly as before.
    /// </para>
    /// </remarks>
    public static string Resolve(DocumentMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.ContentType))
        {
            return metadata.ContentType;
        }

        if (string.IsNullOrWhiteSpace(metadata.FileName))
        {
            return DefaultContentType;
        }

        var inferred = ContentTypeMap.FromFileName(metadata.FileName);
        return string.Equals(inferred, "application/octet-stream", StringComparison.Ordinal)
            ? DefaultContentType
            : inferred;
    }
}
