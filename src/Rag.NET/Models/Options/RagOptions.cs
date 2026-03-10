namespace Rag.NET.Models.Options;

public sealed class RagOptions
{
    public int TopK { get; set; } = 5;
    public double MinScore { get; set; } = 0.0;
    public string? SystemPrompt { get; set; }
    public float? Temperature { get; set; }
}
