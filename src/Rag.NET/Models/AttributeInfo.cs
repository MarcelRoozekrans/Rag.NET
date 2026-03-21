namespace Rag.NET.Models;

/// <summary>
/// Describes a metadata field that the LLM should extract at ingest
/// and/or filter on at query time.
/// </summary>
/// <param name="Name">
/// The metadata key as it will appear in chunk metadata and filter expressions.
/// Must be lowercase snake_case (e.g. <c>"publication_year"</c>).
/// </param>
/// <param name="Description">
/// Human-readable description injected into the LLM prompt to guide extraction or filtering.
/// </param>
public sealed record AttributeInfo(string Name, string Description);
