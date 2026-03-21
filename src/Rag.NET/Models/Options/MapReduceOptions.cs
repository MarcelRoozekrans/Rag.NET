namespace Rag.NET.Models.Options;

public sealed class MapReduceOptions
{
    public int MapConcurrency { get; init; } = 5;
    public string? MapPromptTemplate { get; init; }
    public string? ReducePromptTemplate { get; init; }
}
