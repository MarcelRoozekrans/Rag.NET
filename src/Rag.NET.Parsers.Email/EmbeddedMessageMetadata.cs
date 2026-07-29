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
/// <see cref="ContainerEntryDispatcher"/> already does for attachments — and is distinguished
/// by a composed file name.
/// </para>
/// </remarks>
internal static class EmbeddedMessageMetadata
{
    private const char Separator = '#';
    private const string Fallback = "embedded-message";

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
    /// <remarks>
    /// <para>
    /// The stem goes through <see cref="FileNameSanitizer"/>, which this assembly reaches
    /// without a <c>using</c>: it lives in the bare <c>Rag.NET</c> namespace, so enclosing-scope
    /// lookup from <c>Rag.NET.Parsers.Email</c> finds it. This type kept a private copy of those
    /// rules until Phase 3.6, on the grounds that the shared one was in an unreferenced assembly
    /// — untrue since Phase 2.5 moved it to <c>Rag.NET.Abstractions</c>.
    /// </para>
    /// <para>
    /// Adopting it changed emitted names four ways. The stem now caps at <b>128</b> characters
    /// rather than 64 (the shared default, taken as-is), so long subjects that used to truncate
    /// mid-word survive. A stem that is nothing but invalid characters (<c>"///"</c>) now
    /// collapses to <see cref="Fallback"/> rather than to <c>"___"</c>, which named nothing. And
    /// a name ending in a non-breaking space before a dot no longer keeps that space: the old
    /// <c>TrimEnd('.', ' ')</c> matched two characters in one pass, so stripping the dot
    /// re-exposed whitespace it could not see, where the shared sanitizer trims to a fixed point
    /// over <see cref="char.IsWhiteSpace(char)"/>. <see cref="Fallback"/> is passed rather than
    /// hard-coded there, so the fallback stem is unchanged.
    /// </para>
    /// <para>
    /// The fourth, found in the Phase 3.6 review rather than recorded with the other three: the
    /// two sanitizers <b>order replacement and trimming oppositely</b>. The deleted copy trimmed
    /// first; <see cref="FileNameSanitizer"/> replaces first. TAB, LF, VT, FF and CR are C0
    /// control characters <i>and</i> whitespace, so one at either edge is now substituted to
    /// <c>_</c> before trimming can see it, and <c>_</c> is not whitespace —
    /// <c>"report\t"</c> becomes <c>report_</c> where it used to become <c>report</c>. The
    /// shared ordering is left alone deliberately: four other call sites depend on it, and
    /// replacing before trimming is arguably the more correct rule. Pinned by
    /// <c>EmbeddedMessageNamingTests</c>, which records the pre-3.6 value alongside each case.
    /// </para>
    /// </remarks>
    private static string Compose(string parentFileName, string? name, string extension) =>
        string.Concat(parentFileName, Separator.ToString(), FileNameSanitizer.Sanitize(name, Fallback), extension);
}
