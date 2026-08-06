using Rag.NET.Models;

namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for self-query retrieval: an LLM rewrites the query and, given a schema of available
/// metadata fields, may also generate a metadata filter, before retrieval runs.
/// </summary>
public sealed class SelfQueryOptions
{
    /// <summary>
    /// When provided, the LLM is told which metadata fields are available for filtering.
    /// When null, the LLM filters freely.
    /// </summary>
    public IReadOnlyList<AttributeInfo>? Schema { get; init; }
}
