namespace Rag.NET.Models.Options;

/// <summary>Options for <see cref="Rag.NET.Chunking.CodeChunkingStrategy"/>.</summary>
public sealed class CodeChunkingOptions
{
    /// <summary>
    /// Explicit language name. When <see langword="null"/>, language is auto-detected
    /// from the file extension in <c>DocumentSection.DocumentId.Value</c>.
    /// Valid values: python, javascript, typescript, java, go, rust, ruby, csharp, cpp, php, swift.
    /// Throws <see cref="ArgumentException"/> at construction if set to an unrecognised value.
    /// </summary>
    public string? Language { get; init; }
}
