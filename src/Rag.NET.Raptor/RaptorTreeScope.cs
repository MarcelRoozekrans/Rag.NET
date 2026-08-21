namespace Rag.NET.Raptor;

/// <summary>Controls what set of chunks RAPTOR clusters over when it builds its tree.</summary>
public enum RaptorTreeScope
{
    /// <summary>
    /// Cluster within one document's chunks, at ingestion time.
    /// </summary>
    /// <remarks>
    /// The library's original behaviour. A tree built this way cannot produce a node spanning two
    /// documents, so its summaries answer questions about one document's themes and nothing wider.
    /// Kept selectable rather than removed because it is the control arm Phase 6.2.1 differences
    /// the corpus scope against.
    /// </remarks>
    PerDocument,

    /// <summary>
    /// Cluster across every leaf chunk in the corpus, rebuilt on growth rather than per document.
    /// </summary>
    /// <remarks>
    /// What the RAPTOR paper describes. Requires an <see cref="Store.IRaptorLeafStore"/>, because
    /// the vector store cannot enumerate what it holds. Ingesting a single document no longer
    /// produces a tree immediately: summaries appear once the corpus crosses
    /// <see cref="RaptorOptions.CorpusGrowthThreshold"/> or a rebuild is requested.
    /// </remarks>
    Corpus,
}
