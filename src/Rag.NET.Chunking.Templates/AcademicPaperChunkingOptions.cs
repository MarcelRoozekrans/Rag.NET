namespace Rag.NET.Chunking.Templates;

public sealed class AcademicPaperChunkingOptions
{
    public bool IncludeReferences { get; set; } = false;
    public bool IncludeAbstract { get; set; } = true;
}
