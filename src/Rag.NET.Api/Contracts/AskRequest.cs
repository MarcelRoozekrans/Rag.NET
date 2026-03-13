namespace Rag.NET.Api.Contracts;

public sealed record AskRequest
{
    public required string Query { get; init; }
    public int TopK { get; init; } = 5;
    public bool UseHybridSearch { get; init; } = true;
}
