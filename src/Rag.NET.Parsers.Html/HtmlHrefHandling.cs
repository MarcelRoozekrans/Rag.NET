namespace Rag.NET.Parsers.Html;

/// <summary>What <see cref="HtmlDocumentParser"/> does with a link's <c>href</c> (#371).</summary>
public enum HtmlHrefHandling
{
    /// <summary>
    /// Append the <c>href</c> to the link text — <c>Laatste nieuws (/nieuws/laatste)</c>. The
    /// default, and the only behaviour before #371.
    /// </summary>
    Keep = 0,

    /// <summary>
    /// Drop the <c>href</c> and keep the link text — <c>Laatste nieuws</c>.
    /// </summary>
    /// <remarks>
    /// <b>The URL is removed, not the link's text.</b> Removing the text would delete content the
    /// document actually says, which is the failure #375 was about; a navigation label is still
    /// text worth indexing. Use this when relative URLs are noise in the embedding — which is the
    /// case #371 reports, where they are site-internal paths with no meaning on their own.
    /// </remarks>
    Remove = 1,

    /// <summary>
    /// Resolve a relative <c>href</c> against the document's base and append the result —
    /// <c>Laatste nieuws (https://example.com/nieuws/laatste)</c>.
    /// </summary>
    /// <remarks>
    /// A link that is already absolute is appended unchanged. When no base can be determined the
    /// <c>href</c> is appended as it stands, exactly as <see cref="Keep"/> would: a made-up base
    /// would produce URLs that point nowhere, which is worse than a relative path that is at least
    /// honest about being relative. See <see cref="HtmlParserOptions.BaseUri"/> for where the base
    /// comes from.
    /// </remarks>
    MakeAbsolute = 2,
}
