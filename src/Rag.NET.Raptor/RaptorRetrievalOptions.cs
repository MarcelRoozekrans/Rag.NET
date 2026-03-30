namespace Rag.NET.Raptor;

/// <summary>Configuration for the RAPTOR retrieval behavior.</summary>
public sealed class RaptorRetrievalOptions
{
    /// <summary>Retrieval mode. Default: Blend.</summary>
    public RaptorRetrievalMode Mode { get; set; } = RaptorRetrievalMode.Blend;

    /// <summary>Score multiplier for summary chunks in Boost mode. Default: 1.2.</summary>
    public double SummaryBoostFactor { get; set; } = 1.2;

    /// <summary>Minimum RAPTOR level to include in Filter mode. Null = no lower bound.</summary>
    public int? MinRaptorLevel { get; set; }

    /// <summary>Maximum RAPTOR level to include in Filter mode. Null = no upper bound.</summary>
    public int? MaxRaptorLevel { get; set; }
}
