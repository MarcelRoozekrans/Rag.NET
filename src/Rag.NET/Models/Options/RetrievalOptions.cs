namespace Rag.NET.Models.Options;

public sealed class RetrievalOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public IDictionary<string, string>? MetadataFilter { get; set; }
    public bool UseHybridSearch { get; set; }
    public bool UseLostInTheMiddleReordering { get; set; }
    public bool UseRedundancyFilter { get; set; }
    public float RedundancyThreshold { get; set; } = 0.95f;

    /// <summary>
    /// Set to <see langword="false"/> to skip multi-query expansion for this call,
    /// even when <see cref="Rag.NET.Abstractions.IQueryExpander"/> is registered in DI.
    /// Has no effect when no expander is registered.
    /// </summary>
    public bool UseMultiQuery { get; set; } = true;
}
