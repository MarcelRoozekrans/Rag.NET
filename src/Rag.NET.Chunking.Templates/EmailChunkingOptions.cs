namespace Rag.NET.Chunking.Templates;

/// <summary>
/// Configuration for <see cref="EmailChunkingStrategy"/>, applied per
/// <see cref="Models.DocumentSection"/> heading. Both flags default to <see langword="true"/>,
/// which chunks every section the parser produced.
/// </summary>
public sealed class EmailChunkingOptions
{
    /// <summary>
    /// Whether sections with the <c>headers</c> heading (From/To/Subject/Date) are chunked.
    /// When <see langword="false"/> they are skipped entirely — no chunk, no metadata.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool IncludeHeaders { get; set; } = true;

    /// <summary>
    /// Whether sections with an <c>attachment:&lt;name&gt;</c> heading are chunked.
    /// When <see langword="false"/> they are skipped entirely. Default: <see langword="true"/>.
    /// </summary>
    public bool IncludeAttachments { get; set; } = true;
}
