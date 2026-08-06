using Rag.NET.Models;

namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for LLM-based chunk metadata extraction at ingest time: one LLM call per chunk,
/// extracting structured attributes into that chunk's metadata.
/// </summary>
public sealed class LlmMetadataExtractionOptions
{
    /// <summary>
    /// When provided, the LLM is constrained to extract only these fields.
    /// When null, the LLM extracts freely.
    /// </summary>
    public IReadOnlyList<AttributeInfo>? Schema { get; init; }
}
