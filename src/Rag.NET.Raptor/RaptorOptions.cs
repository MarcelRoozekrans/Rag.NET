using Microsoft.Extensions.AI;

namespace Rag.NET.Raptor;

/// <summary>Configuration for the RAPTOR ingestion behavior.</summary>
public sealed class RaptorOptions
{
    /// <summary>Toggle RAPTOR tree building on/off. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Skip RAPTOR if the document has fewer embedded chunks than this. Default: 5.</summary>
    public int MinChunksForRaptor { get; set; } = 5;

    /// <summary>UMAP target dimensionality for clustering. Default: 10.</summary>
    public int ReducedDimensionality { get; set; } = 10;

    /// <summary>Cap for GMM cluster count. Null = BIC auto-selects. Default: null.</summary>
    public int? MaxClusters { get; set; }

    /// <summary>Cap recursion depth. Null = recurse until 1 cluster remains. Default: null.</summary>
    public int? MaxTreeDepth { get; set; }

    /// <summary>Keep original leaf chunks alongside summaries. Default: true.</summary>
    public bool StoreLeafChunks { get; set; } = true;

    /// <summary>LLM prompt template for cluster summarization. {chunks} is replaced with concatenated text.</summary>
    public string SummaryPrompt { get; set; } = """
        You are a summarization assistant. Below are several related text passages from the same document cluster.
        Write a concise, comprehensive summary that captures all key information.

        Passages:
        {chunks}

        Summary:
        """;

    /// <summary>Optional separate chat client for summaries (e.g. a cheaper model). Null = use DI-registered IChatClient.</summary>
    public IChatClient? SummaryChatClient { get; set; }

    /// <summary>Optional separate embedder for summaries. Null = use DI-registered IEmbeddingGenerator.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? SummaryEmbedder { get; set; }
}
