using Rag.NET.Models;

namespace Rag.NET.Models.Options;

public sealed class SelfQueryOptions
{
    /// <summary>
    /// When provided, the LLM is told which metadata fields are available for filtering.
    /// When null, the LLM filters freely.
    /// </summary>
    public IReadOnlyList<AttributeInfo>? Schema { get; init; }
}
