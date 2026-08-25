namespace Rag.NET.Parsers.Html;

/// <summary>Options for <see cref="HtmlDocumentParser"/>.</summary>
public sealed class HtmlParserOptions
{
    /// <summary>
    /// What to do with each link's <c>href</c>. Defaults to <see cref="HtmlHrefHandling.Keep"/>,
    /// so behaviour is unchanged unless a caller asks for something else.
    /// </summary>
    public HtmlHrefHandling HrefHandling { get; set; } = HtmlHrefHandling.Keep;

    /// <summary>
    /// Fallback base for <see cref="HtmlHrefHandling.MakeAbsolute"/>, used only when the document
    /// itself does not say where it came from.
    /// </summary>
    /// <remarks>
    /// <para>The base is looked for in this order, and the first one found wins:</para>
    /// <list type="number">
    /// <item><description>
    /// a <c>&lt;base href&gt;</c> element in the document — HTML's own mechanism, and authoritative
    /// when present because the page is stating its own base;
    /// </description></item>
    /// <item><description>
    /// the document's <c>url</c> tag. Every web data provider in this library — crawler, sitemap
    /// and RSS — already records the page URL under that key, so this is set for web-ingested
    /// content without anyone configuring anything;
    /// </description></item>
    /// <item><description>this property.</description></item>
    /// </list>
    /// <para>
    /// It is a fallback rather than the primary source because one configured URI cannot be right
    /// for a crawl spanning many pages: the base has to be per-document, and the two sources above
    /// are.
    /// </para>
    /// </remarks>
    public Uri? BaseUri { get; set; }
}
