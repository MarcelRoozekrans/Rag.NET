namespace Rag.NET.GraphRag;

/// <summary>Controls which GraphRAG search strategy is used at retrieval time.</summary>
public enum GraphRagRetrievalMode
{
    /// <summary>Entity-hop traversal — start from matched entities, traverse neighbors, collect context.</summary>
    Local,

    /// <summary>Map-reduce over community reports — broad thematic questions across the full corpus.</summary>
    Global,

    /// <summary>LLM classifies the query and routes to Local or Global automatically.</summary>
    Auto,
}
