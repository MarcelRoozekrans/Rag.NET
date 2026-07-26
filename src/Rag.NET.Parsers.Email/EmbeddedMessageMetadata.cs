using System.Buffers;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Shapes the <see cref="DocumentMetadata"/> an embedded message is parsed under.
/// </summary>
/// <remarks>
/// <para>
/// This is the only piece the EML and MSG recursion paths share. They cannot share the
/// recursion itself: a nested message arrives as a live in-memory object
/// (<c>MimeKit.MessagePart.Message</c>, <c>MsgReader.Outlook.Storage.Message</c>) owned by the
/// parent's <c>using</c>, not as a stream, so neither can re-enter the stream-based
/// <c>ParseAsync</c>.
/// </para>
/// <para>
/// The nested message keeps the parent's <see cref="DocumentId"/> — matching what
/// <see cref="EmailAttachmentDispatcher"/> already does for attachments — and is distinguished
/// by a composed file name.
/// </para>
/// </remarks>
internal static class EmbeddedMessageMetadata
{
    private const char Separator = '#';
    private const char Replacement = '_';
    private const int MaxNameLength = 64;
    private const string Fallback = "embedded-message";

    /// <summary>
    /// Characters replaced in the composed name. This is the Windows-invalid superset, pinned
    /// rather than read from <see cref="Path.GetInvalidFileNameChars"/> so the name does not
    /// vary with the host OS.
    /// </summary>
    /// <remarks>
    /// <c>Rag.NET.DataProviders.FileNameSanitizer</c> does the same job with more rules, but
    /// this assembly does not reference <c>Rag.NET.DataProviders</c> and adding a package
    /// dependency from a parser to the connector assembly for one call is not worth it. Kept
    /// deliberately minimal: this name is metadata for display and provenance, never a path.
    /// </remarks>
    private static readonly SearchValues<char> InvalidChars = BuildInvalidChars();

    public static DocumentMetadata Create(DocumentMetadata parent, string? name, string extension, string contentType) =>
        new()
        {
            DocumentId = parent.DocumentId,
            FileName = Compose(parent.FileName, name, extension),
            ContentType = contentType,
            Tags = parent.Tags,
            CreatedAt = parent.CreatedAt,
        };

    /// <summary>
    /// Builds <c>parent.eml#Forwarded Subject.eml</c>. The parent name is already sanitized —
    /// it is either the caller's own file name or a name this method produced.
    /// </summary>
    private static string Compose(string parentFileName, string? name, string extension) =>
        string.Concat(parentFileName, Separator.ToString(), Sanitize(name), extension);

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Fallback;

        // string.Trim rather than ReadOnlySpan.Trim: the span extension takes its receiver by
        // value and trips the EPS06 hidden-struct-copy analyzer, which is an error here.
        var trimmed = name.Trim();
        int length = trimmed.Length;
        if (length > MaxNameLength)
        {
            length = MaxNameLength;

            // Never split a surrogate pair: a lone high surrogate renders as U+FFFD.
            if (char.IsHighSurrogate(trimmed[length - 1]))
                length--;
        }

        var cleaned = string.Create(length, trimmed, static (destination, source) =>
        {
            for (int i = 0; i < destination.Length; i++)
                destination[i] = InvalidChars.Contains(source[i]) ? Replacement : source[i];
        });

        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length > 0 ? cleaned : Fallback;
    }

    private static SearchValues<char> BuildInvalidChars()
    {
        const string punctuation = "<>:\"/\\|?*";
        const int controlCharCount = 0x20;

        Span<char> chars = stackalloc char[punctuation.Length + controlCharCount];
        punctuation.CopyTo(chars);
        for (int i = 0; i < controlCharCount; i++)
            chars[punctuation.Length + i] = (char)i;

        return SearchValues.Create(chars);
    }
}
