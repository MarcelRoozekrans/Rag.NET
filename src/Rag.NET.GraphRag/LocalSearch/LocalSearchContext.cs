namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// An assembled local-search context: the text a prompt reads, plus what it cost and what did not
/// fit.
/// </summary>
/// <remarks>
/// The counts are not diagnostics-for-their-own-sake. A section silently arriving empty is exactly
/// how this library shipped a local search with no relationships in it for months, so the shape of
/// what was built is reported alongside it rather than left to be inferred from the string.
/// </remarks>
public sealed record LocalSearchContext
{
    /// <summary>The rendered context — the sections, in specification order, separated by blank lines.</summary>
    public required string Text { get; init; }

    /// <summary>Total <c>cl100k_base</c> tokens in <see cref="Text"/>.</summary>
    public required int TokenCount { get; init; }

    /// <summary>Community reports rendered, of those offered.</summary>
    public required SectionFill Reports { get; init; }

    /// <summary>Entities rendered, of those selected.</summary>
    public required SectionFill Entities { get; init; }

    /// <summary>Relationships rendered, of those that survived selection.</summary>
    public required SectionFill Relationships { get; init; }

    /// <summary>Source chunks rendered, of those the selected entities named.</summary>
    public required SectionFill Sources { get; init; }

    /// <summary>Conversation turns rendered, of those offered.</summary>
    public required SectionFill History { get; init; }
}
