namespace Rag.NET.Models.Options;

public class ParentDocumentOptions
{
    public int ParentChunkSize { get; set; } = 2048;
    public int ParentOverlap { get; set; } = 100;
}
