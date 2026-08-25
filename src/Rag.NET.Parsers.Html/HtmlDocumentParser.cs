using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Html;

public sealed class HtmlDocumentParser : IDocumentParser, IDeclaresContentTypes
{
    private const string HtmlContentType = "text/html";

    private static readonly HashSet<string> s_headingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6",
    };

    /// <summary>
    /// Tags after which a line break is emitted, so paragraph structure survives into the section
    /// text. The old sibling walk got this free from <c>AppendLine</c> per sibling element; a
    /// text-node walk has to say it.
    /// </summary>
    private static readonly HashSet<string> s_blockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "ul", "ol", "tr", "td", "th", "table",
        "section", "article", "aside", "main", "blockquote", "pre", "figure", "figcaption", "dl", "dt", "dd",
    };

    private static readonly string s_removeSelector = string.Join(", ", new[] { "script", "style", "nav", "footer", "header" });

    /// <inheritdoc/>
    public static IReadOnlyCollection<string> ContentTypes { get; } = [HtmlContentType];

    public bool CanParse(string contentType) =>
        contentType.Equals(HtmlContentType, StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<DocumentSection> ParseAsync(
        Stream stream,
        DocumentMetadata metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(stream, cancellationToken).ConfigureAwait(false);

        RemoveNonContentElements(document);
        ConvertLinksToTextUrl(document);

        var body = document.Body;
        if (body is null)
        {
            yield break;
        }

        foreach (var section in BuildSections(body, metadata.DocumentId, cancellationToken))
        {
            yield return section;
        }
    }

    /// <summary>
    /// Splits the body into one section per heading, each carrying the text between that heading
    /// and the next one in <b>document order</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to follow <c>heading.NextElementSibling</c>, which is a sibling relation and not a
    /// document-order one. A heading wrapped for layout — <c>&lt;div&gt;&lt;div&gt;&lt;h1&gt;</c>,
    /// the ordinary shape in component-framework markup — is its parent's only child, so the walk
    /// ended before it started and every following paragraph was <b>dropped</b>: not misfiled, never
    /// emitted (#375). Text before the first heading was lost the same way, because only headings
    /// produced sections.
    /// </para>
    /// <para>
    /// Walking AngleSharp's own document-order traversal removes the nesting question entirely.
    /// Accumulating <b>text nodes</b> rather than elements' <c>TextContent</c> is what makes that
    /// safe: text nodes are leaves, so a container and its children can never both contribute.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<DocumentSection> BuildSections(
        IElement body, DocumentId documentId, CancellationToken cancellationToken)
    {
        var open = new OpenSection();
        var sections = new List<DocumentSection>();

        foreach (var node in body.Descendants())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case IElement element when s_headingTags.Contains(element.TagName):
                    Close(sections, open, documentId);
                    open.Start(element);
                    break;

                case IElement element when s_blockTags.Contains(element.TagName):
                    // Keeps paragraph structure, which the chunkers downstream split on.
                    open.Break();
                    break;

                case IText text when !IsInsideHeading(text):
                    open.Append(text.Data);
                    break;

                default:
                    break;
            }
        }

        Close(sections, open, documentId);
        return sections;
    }

    private static void Close(List<DocumentSection> sections, OpenSection open, DocumentId documentId)
    {
        if (open.TryBuild(documentId, sections.Count, out var section))
        {
            sections.Add(section);
        }
    }

    /// <summary>
    /// Whether this text belongs to a heading element, whose text the section already carries.
    /// </summary>
    private static bool IsInsideHeading(INode node)
    {
        for (var parent = node.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (s_headingTags.Contains(parent.TagName))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveNonContentElements(IDocument document)
    {
        var elements = document.QuerySelectorAll(s_removeSelector).ToList();
        for (int i = 0; i < elements.Count; i++)
        {
            elements[i].Remove();
        }
    }

    private static void ConvertLinksToTextUrl(IDocument document)
    {
        var links = document.QuerySelectorAll("a[href]").ToList();
        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var href = link.GetAttribute("href");
            var text = link.TextContent.Trim();
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(href))
            {
                link.TextContent = $"{text} ({href})";
            }
        }
    }

    /// <summary>
    /// The section currently being accumulated. Its heading is null until the first heading is
    /// seen, which is how text before any heading becomes a section of its own instead of being
    /// discarded — the second half of #375.
    /// </summary>
    private sealed class OpenSection
    {
        private readonly StringBuilder _text = new();
        private IElement? _heading;

        public void Start(IElement heading)
        {
            _heading = heading;
            _text.Clear();
            _text.Append(heading.TextContent.Trim());
            Break();
        }

        /// <summary>
        /// Appends a text node, collapsing every run of whitespace to one space.
        /// </summary>
        /// <remarks>
        /// Collapsing happens <b>here</b>, not at the end. A newline inside HTML text is ordinary
        /// whitespace and not a line break — a paragraph split across source lines is still one
        /// paragraph — so normalising later, once block breaks have themselves been written as
        /// newlines, cannot tell the two apart. It turns source formatting into paragraph
        /// structure, which is what the first attempt here did.
        /// </remarks>
        public void Append(string data)
        {
            foreach (char c in data)
            {
                if (!char.IsWhiteSpace(c))
                {
                    _text.Append(c);
                    continue;
                }

                // One space per run, and never one that follows a break or another space.
                if (_text.Length > 0 && _text[^1] != ' ' && _text[^1] != '\n')
                {
                    _text.Append(' ');
                }
            }
        }

        /// <summary>Ends the current block, so the next text starts a new line.</summary>
        public void Break()
        {
            while (_text.Length > 0 && _text[^1] == ' ')
            {
                _text.Length--;
            }

            if (_text.Length > 0 && _text[^1] != '\n')
            {
                _text.Append('\n');
            }
        }

        public bool TryBuild(DocumentId documentId, int sectionIndex, out DocumentSection section)
        {
            var text = _text.ToString().Trim();
            if (text.Length == 0)
            {
                section = null!;
                return false;
            }

            section = new DocumentSection
            {
                Text = text,
                DocumentId = documentId,
                SectionIndex = sectionIndex,
                Heading = _heading?.TextContent.Trim(),
                HeadingLevel = _heading is null ? null : _heading.TagName[1] - '0',
            };

            _text.Clear();
            _heading = null;
            return true;
        }

    }
}
