using System.Text.Json.Serialization;

namespace Rag.NET.GraphRag;

/// <summary>A node in a hierarchical mind-map tree extracted from document content.</summary>
[method: JsonConstructor]
public sealed record MindMapNode(string Title, string Summary, IReadOnlyList<MindMapNode> Children);
