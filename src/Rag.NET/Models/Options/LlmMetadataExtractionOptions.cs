using Rag.NET.Models;

namespace Rag.NET.Models.Options;

public sealed class LlmMetadataExtractionOptions
{
    /// <summary>
    /// When provided, the LLM is constrained to extract only these fields.
    /// When null, the LLM extracts freely.
    /// </summary>
    public IReadOnlyList<AttributeInfo>? Schema { get; init; }
}
