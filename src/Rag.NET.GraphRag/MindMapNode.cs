namespace Rag.NET.GraphRag;

/// <summary>A node in a hierarchical mind-map tree extracted from document content.</summary>
public sealed record MindMapNode(string Title, string Summary, IReadOnlyList<MindMapNode> Children);
