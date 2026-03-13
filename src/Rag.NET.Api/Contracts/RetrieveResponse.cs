namespace Rag.NET.Api.Contracts;

public sealed record RetrieveResponse(IReadOnlyList<SearchResultDto> Results);
