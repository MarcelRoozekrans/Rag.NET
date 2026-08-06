namespace Rag.NET.Models.Options;

/// <summary>Expirations for the embedding and result caches, active once <c>RagBuilder.UseCaching()</c> is registered.</summary>
public class CachingOptions
{
    /// <summary>How long a cached query embedding stays valid. Defaults to 30 minutes.</summary>
    public TimeSpan EmbeddingTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a cached retrieval result stays valid. Defaults to 5 minutes.</summary>
    public TimeSpan ResultTtl { get; set; } = TimeSpan.FromMinutes(5);
}
