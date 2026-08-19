namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>One turn of the conversation a local-search query arrives in.</summary>
/// <remarks>
/// Local search folds recent history into the context so a follow-up question resolves against
/// what was already said. See section 9 of
/// <c>docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md</c> for the reading of
/// upstream this follows.
/// </remarks>
/// <param name="Role">Who spoke.</param>
/// <param name="Content">What was said.</param>
public sealed record ConversationTurn(ConversationRole Role, string Content);
