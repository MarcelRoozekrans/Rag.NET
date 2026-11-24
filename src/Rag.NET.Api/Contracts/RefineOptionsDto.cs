namespace Rag.NET.Api.Contracts;

/// <summary><c>RefineOptions</c> on the wire. A field left out takes the in-process default.</summary>
public sealed record RefineOptionsDto
{
    /// <summary>Mirrors <c>RefineOptions.InitialPromptTemplate</c>. Prompt-shaping.</summary>
    public string? InitialPromptTemplate { get; init; }

    /// <summary>Mirrors <c>RefineOptions.RefinePromptTemplate</c>. Prompt-shaping.</summary>
    public string? RefinePromptTemplate { get; init; }
}
