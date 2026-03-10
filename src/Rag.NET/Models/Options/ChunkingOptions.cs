namespace Rag.NET.Models.Options;

public sealed class ChunkingOptions
{
    public int MaxChunkSize { get; set; } = 512;
    public int Overlap { get; set; } = 50;
}
