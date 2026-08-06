namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for parent-document retrieval: small child chunks are embedded and searched, but the
/// larger parent chunk they came from is what gets returned to the caller (see
/// <see cref="Rag.NET.Abstractions.IParentChunkStore"/>), giving the answer engine more
/// surrounding context than the matched child chunk alone.
/// </summary>
public sealed class ParentDocumentOptions
{
    /// <summary>
    /// Target size in characters of the larger parent chunks child chunks are grouped into.
    /// Default 2048.
    /// </summary>
    public int ParentChunkSize { get; set; } = 2048;

    /// <summary>Characters of overlap between consecutive parent chunks. Default 100.</summary>
    public int ParentOverlap { get; set; } = 100;
}
