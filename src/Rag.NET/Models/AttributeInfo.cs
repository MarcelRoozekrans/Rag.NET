namespace Rag.NET.Models;

/// <summary>
/// Describes a metadata field that the LLM should extract at ingest
/// and/or filter on at query time.
/// </summary>
public sealed record AttributeInfo(string Name, string Description);
