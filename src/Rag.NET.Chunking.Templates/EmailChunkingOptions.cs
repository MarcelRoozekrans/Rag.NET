namespace Rag.NET.Chunking.Templates;

public sealed class EmailChunkingOptions
{
    public bool IncludeHeaders { get; set; } = true;
    public bool IncludeAttachments { get; set; } = true;
}
