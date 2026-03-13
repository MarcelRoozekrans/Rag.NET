namespace Rag.NET.Api.Contracts;

public sealed record AskResponse(string Answer, IReadOnlyList<SearchResultDto> Sources);
