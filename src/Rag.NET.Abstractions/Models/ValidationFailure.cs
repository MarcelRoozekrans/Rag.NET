namespace Rag.NET.Models;

/// <summary>A single validation rule failure. Produced by the facade boundary validator.</summary>
public sealed record ValidationFailure(string PropertyName, string ErrorMessage);
