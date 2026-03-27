namespace Rag.NET.Models.Options;

public sealed class PersistentMemoryOptions
{
    /// <summary>Maximum number of past exchange pairs to retrieve from the vector store per query.</summary>
    public int TopK { get; set; } = 3;

    /// <summary>Minimum similarity score threshold. Matches below this value are not injected.</summary>
    public float MinScore { get; set; } = 0.7f;
}
