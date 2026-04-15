namespace Rag.NET.Models.Options;

/// <summary>
/// Controls how <see cref="Rag.NET.Models.Options.RetrievalOptions.CragFallbackMode"/> merges
/// web search results when CRAG relevance drops below <see cref="RetrievalOptions.CragScoreThreshold"/>.
/// </summary>
public enum CragFallbackMode
{
    /// <summary>Discard vector results and return only web results.</summary>
    Replace,

    /// <summary>Append web results after the surviving vector results.</summary>
    Append
}
