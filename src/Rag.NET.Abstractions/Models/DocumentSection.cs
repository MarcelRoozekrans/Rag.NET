namespace Rag.NET.Models;

/// <summary>
/// A document broken into its structural pieces by an <c>IDocumentParser</c> — a heading's
/// content, a page, a slide, and so on, depending on the source format — before chunking further
/// splits each section's text into <see cref="TextChunk"/>s.
/// </summary>
public sealed record DocumentSection
{
    /// <summary>The section's raw text content, as extracted by the parser.</summary>
    public required string Text { get; init; }

    /// <summary>The document this section belongs to.</summary>
    public required DocumentId DocumentId { get; init; }

    /// <summary>
    /// The heading's nesting depth (1 for a top-level heading, 2 for a sub-heading, and so on).
    /// <see langword="null"/> when the source format has no heading structure the parser
    /// recognised for this section, not the same as depth 0.
    /// </summary>
    public int? HeadingLevel { get; init; }

    /// <summary>
    /// The heading text that introduces this section. <see langword="null"/> when the source
    /// format has no heading structure the parser recognised for this section.
    /// </summary>
    public string? Heading { get; init; }

    /// <summary>
    /// The 1-based source page this section came from. <see langword="null"/> for formats with no
    /// page concept (for example Markdown or plain text).
    /// </summary>
    public int? PageNumber { get; init; }

    /// <summary>
    /// This section's 0-based position among all sections the parser produced for its document,
    /// in document reading order.
    /// </summary>
    public int SectionIndex { get; init; }
}
