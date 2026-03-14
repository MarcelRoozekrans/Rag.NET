namespace Rag.NET.Models.Options;

public class CachingOptions
{
    public TimeSpan EmbeddingTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ResultTtl { get; set; } = TimeSpan.FromMinutes(5);
}
