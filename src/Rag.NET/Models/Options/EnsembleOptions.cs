namespace Rag.NET.Models.Options;

public sealed class EnsembleOptions
{
    public float DenseWeight { get; init; } = 0.5f;
    public float Bm25Weight  { get; init; } = 0.5f;
    public int   K           { get; init; } = 60;
}
