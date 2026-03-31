namespace Rag.NET.Chunking.CSharp;

public sealed class CSharpChunkingOptions
{
    /// <summary>Include private members. Default: false.</summary>
    public bool IncludePrivateMembers { get; set; } = false;

    /// <summary>Include internal members. Default: true.</summary>
    public bool IncludeInternalMembers { get; set; } = true;

    /// <summary>
    /// Include member bodies. When false, only the signature and XML doc comment are included.
    /// Default: true.
    /// </summary>
    public bool IncludeBodies { get; set; } = true;
}
