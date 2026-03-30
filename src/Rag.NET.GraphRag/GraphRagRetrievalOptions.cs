using Microsoft.Extensions.AI;

namespace Rag.NET.GraphRag;

/// <summary>Configuration for GraphRAG retrieval behaviors.</summary>
public sealed class GraphRagRetrievalOptions
{
    /// <summary>Search mode. Default: Local.</summary>
    public GraphRagRetrievalMode Mode { get; set; } = GraphRagRetrievalMode.Local;

    /// <summary>Hop depth for local entity traversal. Default: 1.</summary>
    public int LocalSearchDepth { get; set; } = 1;

    /// <summary>Top-K entities to start local traversal from. Default: 10.</summary>
    public int LocalTopEntities { get; set; } = 10;

    /// <summary>Blend weight for PageRank vs. vector similarity in scoring. Default: 0.3.</summary>
    public double PageRankWeight { get; set; } = 0.3;

    /// <summary>Reports per batch in global map phase. Null = auto. Default: null.</summary>
    public int? GlobalBatchSize { get; set; }

    /// <summary>Optional model for global map-reduce. Null = use DI-registered IChatClient.</summary>
    public IChatClient? GlobalChatClient { get; set; }
}
