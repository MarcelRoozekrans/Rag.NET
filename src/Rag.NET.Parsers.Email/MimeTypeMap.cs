namespace Rag.NET.Parsers.Email;

/// <summary>
/// Minimal file-extension → MIME-type map used when an Outlook attachment does not carry
/// an explicit MIME type (see <see cref="MsgDocumentParser"/>). Covers the content types
/// handled by the Rag.NET parser packages; unknown extensions map to
/// <c>application/octet-stream</c>, which no parser claims, so those attachments are
/// skipped with a warning by <see cref="EmailAttachmentDispatcher"/>.
/// </summary>
internal static class MimeTypeMap
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
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".tiff"] = "image/tiff",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".mp4"] = "audio/mp4",
    };

    public static string FromFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return s_map.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";
    }
}
