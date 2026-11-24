namespace Rag.NET.Api.Contracts;

/// <summary><c>MapReduceOptions</c> on the wire. A field left out takes the in-process default.</summary>
public sealed record MapReduceOptionsDto
{
    /// <summary>Mirrors <c>MapReduceOptions.MapConcurrency</c>.</summary>
    public int? MapConcurrency { get; init; }

    /// <summary>Mirrors <c>MapReduceOptions.MapPromptTemplate</c>. Prompt-shaping.</summary>
    public string? MapPromptTemplate { get; init; }

    /// <summary>Mirrors <c>MapReduceOptions.ReducePromptTemplate</c>. Prompt-shaping.</summary>
    public string? ReducePromptTemplate { get; init; }
}
