namespace Rag.NET;

/// <summary>
/// Minimal file-extension → MIME-type map, used wherever a file name is all there is to type a
/// document by: an Outlook attachment without an explicit MIME type, a ZIP entry, a CLI ingest
/// and a pipeline document with no declared content type. Covers the content types handled by
/// the Rag.NET parser packages; unknown extensions map to <c>application/octet-stream</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fallback assumes no parser claims it.</b> On that assumption an attachment of an
/// unknown type is skipped with a warning by <c>ContainerEntryDispatcher</c>, which is
/// the "degrades rather than breaks" behaviour <c>EmailParserOptions</c> documents.
/// <c>application/octet-stream</c> means "unknown binary", so nothing format-specific should
/// answer to it; a parser that does is guessing, and a wrong guess costs the attachment rather
/// than missing it.
/// </para>
/// <para>
/// <b>The assumption held again from Phase 3.11.</b> Until then
/// <c>Rag.NET.Chunking.Templates</c> registered two parsers whose <c>CanParse</c> accepted
/// <c>application/octet-stream</c> into the same <see cref="Abstractions.IDocumentParser"/>
/// collection the dispatcher searches, so an unknown extension was routed to a parser that could
/// not read it. Both clauses are gone, and the invariant is load-bearing rather than incidental:
/// it is what makes an unclaimed attachment a warning instead of a failure.
/// </para>
/// <para>
/// <b>The Phase 3.9 addendum this replaces attributed that failure to this type, which is wrong.</b>
/// On the email path <c>FromFileName</c> has one caller, <c>StorageMessageAdapter</c> on the
/// <c>.msg</c> route, so an extension-table miss is the only email failure this map is
/// responsible for. Its other callers are <c>DocumentContentTypeResolver</c>,
/// <c>ZipDocumentParser</c> and the CLI's <c>IngestCommand</c>. An <c>.eml</c> attachment never
/// touches it: <c>MimeMessageAdapter.Enumerate</c> builds the type from the part's own MIME
/// headers, so it is
/// <c>application/octet-stream</c> because the sending client wrote that, which is both commoner
/// and less avoidable than a lookup miss. Both routes produce the same type and both were broken;
/// only the first is this map's doing. The invariant matters for both, which is why it is recorded
/// here — but the defect was never a defect of this table.
/// </para>
/// </remarks>
public static class ContentTypeMap
{
    private static readonly Dictionary<string, string> s_map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".epub"] = "application/epub+zip",
        [".eml"] = "message/rfc822",
        [".msg"] = "application/vnd.ms-outlook",
        // Added in Phase 3.10, and it is load-bearing rather than tidy: this map is how
        // ZipDocumentParser types an entry, so without it a zip inside a zip resolved to
        // application/octet-stream, matched no parser, and was warn-and-skipped. That looks like the
        // designed degradation and is not — ContainerContentTypes lists application/zip as something
        // the shared budget must count, and an entry that never reaches a parser never counts.
        [".zip"] = "application/zip",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        // image/tiff is still unclaimed unless the Azure Document Intelligence parser is registered;
        // an unclaimed TIFF is warn-and-skipped by the dispatcher instead of being parsed as text.
        // .tif, .heif and .heic need an entry for the same reason. A miss here answers
        // application/octet-stream, and DocumentContentTypeResolver.ResolveContentType turns that
        // into text/plain for a pipeline document with no declared type, which would ingest a
        // scan as text.
        [".tiff"] = "image/tiff",
        [".tif"] = "image/tiff",
        [".heif"] = "image/heif",
        [".heic"] = "image/heic",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".webm"] = "video/webm",
    };

    /// <summary>
    /// Maps a file name's extension to a MIME type. Unknown or missing extensions return
    /// <c>"application/octet-stream"</c> rather than <see langword="null"/> or throwing.
    /// </summary>
    public static string FromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return s_map.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";
    }
}
