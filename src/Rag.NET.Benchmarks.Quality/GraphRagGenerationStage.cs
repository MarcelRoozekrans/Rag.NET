namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Which GraphRAG stage the generation tool generates and caches on this invocation.
/// <para>
/// <b>Two stages rather than one run doing both, because the second cannot start until the first is
/// finished for the whole corpus.</b> A community report is written about a community, and Leiden
/// only sees a community once every article's entities and relationships are in one graph — so a
/// tool that generated reports as it extracted would be summarising a graph that was still growing,
/// and every report but the last pass's would be paid for and thrown away. Naming the stage makes
/// the ordering a command line rather than a convention.
/// </para>
/// <para>
/// <b><see cref="Extraction"/> is the default, so every invocation that predates this flag still
/// means what it meant.</b> The two stages also write into different directories of
/// <see cref="GraphExtractionCache"/>, which is what lets either be counted, resumed or discarded
/// without disturbing the other.
/// </para>
/// </summary>
public enum GraphRagGenerationStage
{
    /// <summary>
    /// Entity and relationship extraction: one call per chunk plus its gleaning pass, written into
    /// <see cref="GraphExtractionCache.DirectoryName"/> — the default.
    /// </summary>
    Extraction = 0,

    /// <summary>
    /// Community-report generation: one call per community over the graph the extractions build,
    /// written into <see cref="GraphExtractionCache.ReportsDirectoryName"/>.
    /// </summary>
    /// <remarks>
    /// It replays extraction refuse-on-miss to rebuild that graph and never extracts anything
    /// itself. A stage that could re-extract would blend a second generation run's entities into
    /// the graph the reports describe, and the reports would then be summaries of a graph no guard
    /// can rebuild.
    /// </remarks>
    Reports = 1,
}
