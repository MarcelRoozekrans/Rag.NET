namespace Rag.NET.Models;

/// <summary>The kind of LLM call a cost entry was produced by.</summary>
public enum CostKind
{
    /// <summary>A chat-completion call (streaming or not).</summary>
    Chat,

    /// <summary>An embedding-generation call.</summary>
    Embedding,
}
