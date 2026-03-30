namespace Rag.NET.Raptor;

/// <summary>Controls how RAPTOR summary chunks participate in retrieval scoring.</summary>
public enum RaptorRetrievalMode
{
    /// <summary>All levels participate via vector similarity naturally. No score adjustment.</summary>
    Blend,

    /// <summary>Multiply scores of summary chunks (raptor_level &gt; 0) by SummaryBoostFactor.</summary>
    Boost,

    /// <summary>Restrict results to specific RAPTOR tree levels via MinRaptorLevel / MaxRaptorLevel.</summary>
    Filter,
}
