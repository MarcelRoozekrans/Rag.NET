using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

[Validate]
public sealed class ChunkingOptions
{
    [GreaterThan(0)] public int MaxChunkSize { get; set; } = 512;
    [GreaterThan(0)] public int Overlap { get; set; } = 50;
}
