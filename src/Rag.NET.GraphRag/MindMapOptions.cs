using Microsoft.Extensions.AI;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for mind-map extraction.</summary>
public sealed class MindMapOptions
{
    /// <summary>Run extraction automatically at ingestion time. Default: false.</summary>
    public bool ExtractAtIngestion { get; set; } = false;

    /// <summary>Maximum depth of the generated concept tree. Default: 3.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Optional cheaper model override. Null = use DI-registered IChatClient.</summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>LLM prompt template. {text} and {depth} are replaced at runtime.</summary>
    public string Prompt { get; set; } = """
        Analyze the following text and build a hierarchical mind-map of its key concepts.
        Return a JSON object representing the root node with this exact structure:
        {"title": "...", "summary": "...", "children": [...]}
        Each node has: title (short label), summary (1-2 sentence description), children (array of child nodes, same structure).
        Maximum depth: {depth} levels. Aim for 3-7 children per node. Be concise.
        Return only valid JSON, no markdown, no explanation.

        Text:
        {text}
        """;
}
