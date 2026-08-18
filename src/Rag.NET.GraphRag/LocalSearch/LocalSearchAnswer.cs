namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>An answer generated from a local-search context, with the context it came from.</summary>
/// <remarks>
/// The context is returned rather than discarded because the answer's citations refer to it by row:
/// <c>[Data: Entities (3); Sources (0, 1)]</c> names the <c>id</c> column of the tables in
/// <see cref="LocalSearchContext.Text"/>. Without the context the citations resolve to nothing.
/// </remarks>
/// <param name="Answer">The model's response.</param>
/// <param name="Context">The context window it was generated from.</param>
public sealed record LocalSearchAnswer(string Answer, LocalSearchContext Context);
