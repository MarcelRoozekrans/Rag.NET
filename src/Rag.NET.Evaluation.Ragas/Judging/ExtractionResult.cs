namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>The outcome of asking the model for a JSON array of strings.</summary>
/// <param name="Items">The parsed items. Empty is legitimate — an answer can assert nothing.</param>
/// <param name="Parsed">
/// Whether the reply parsed at all. This is the distinction the pre-3.1 code lacked: it caught
/// <c>JsonException</c>, returned an empty list, and the caller scored the empty list as 1.0. A
/// malformed reply therefore produced the best possible score.
/// </param>
internal readonly record struct ExtractionResult(IReadOnlyList<string> Items, bool Parsed)
{
    /// <summary>The reply could not be read as a JSON array of strings.</summary>
    public static ExtractionResult Failed() => new(Array.Empty<string>(), Parsed: false);

    /// <summary>The reply parsed, yielding <paramref name="items"/> (possibly none).</summary>
    /// <param name="items">The items the model listed.</param>
    public static ExtractionResult Success(IReadOnlyList<string> items) => new(items, Parsed: true);
}
