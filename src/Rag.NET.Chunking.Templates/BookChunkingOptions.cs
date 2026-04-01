namespace Rag.NET.Chunking.Templates;

public sealed class BookChunkingOptions
{
    public int MaxDepth { get; set; } = 2;
    public bool IncludeIndex { get; set; } = false;
    public bool IncludeForeword { get; set; } = true;
}
