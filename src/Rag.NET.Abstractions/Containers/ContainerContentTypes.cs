namespace Rag.NET;

/// <summary>
/// The content types that are themselves containers, and therefore count against the shared
/// nesting depth and entry budget when one is found inside another.
/// </summary>
/// <remarks>
/// <para>
/// <b>This set has to be global, and that is not a convenience.</b> The budget is one total across
/// a whole document, so the set of things that consume it must be known in one place. An
/// <c>.eml</c> carrying a <c>.zip</c> is dispatched by the email parser, which will never know
/// anything about archives; if each package tested only its own formats, a
/// <c>zip → .eml → zip</c> chain would have every level counted by whichever package happened to
/// own it and none of them counting the whole. Two bounds that each look correct in isolation, and
/// an attacker who alternates formats walks straight through both.
/// </para>
/// <para>
/// <b>The trap this creates, stated where it will be found:</b> a new container format that is not
/// listed here is <i>not bounded at all</i>, and nothing will complain. Its parser will work, its
/// own tests will pass, and a chain alternating it with a listed format will be unbounded. Adding
/// the content type here is not optional bookkeeping — it is the whole enforcement.
/// </para>
/// <para>
/// Holding these literals in <c>Rag.NET.Abstractions</c> is not a dependency on the packages that
/// parse them. A content type is a string; the parser that claims it lives elsewhere and may not be
/// registered at all.
/// </para>
/// </remarks>
public static class ContainerContentTypes
{
    /// <summary>RFC 5322 email — <c>Rag.NET.Parsers.Email</c>.</summary>
    public const string Eml = "message/rfc822";

    /// <summary>Outlook message — <c>Rag.NET.Parsers.Email</c>.</summary>
    public const string Msg = "application/vnd.ms-outlook";

    /// <summary>ZIP archive — <c>Rag.NET.Parsers.Archive</c>.</summary>
    public const string Zip = "application/zip";

    /// <summary>
    /// ZIP archive as older Windows and Internet Explorer label it. Common enough in real mail that
    /// omitting it would leave a routine archive unbounded.
    /// </summary>
    public const string ZipCompressed = "application/x-zip-compressed";

    /// <summary>
    /// Reports whether a content type is a nested container, and so counts against the shared
    /// limits. Compared case-insensitively, because a content type arriving from a MIME header or a
    /// remote connector carries whatever casing the sender chose.
    /// </summary>
    public static bool IsNestedContainer(string contentType) =>
        contentType.Equals(Eml, StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals(Msg, StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals(Zip, StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals(ZipCompressed, StringComparison.OrdinalIgnoreCase);
}
