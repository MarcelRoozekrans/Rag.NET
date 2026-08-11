namespace Rag.NET.DataProviders;

/// <summary>Base options shared by all cloud storage data providers.</summary>
public abstract class CloudStorageOptions
{
    /// <summary>
    /// File extensions to include (e.g. <c>[".md", ".pdf"]</c>).
    /// Defaults to <c>["*"]</c> which matches all extensions.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; set; } = ["*"];

    /// <summary>Optional predicate to include files by provider-specific ID (may be a path or opaque key depending on the connector). Return <c>false</c> to exclude.</summary>
    public Func<string, bool>? Filter { get; set; }

    /// <summary>
    /// Opaque cursor string for delta runs (format is connector-specific).
    /// <c>null</c> triggers a full traversal.
    /// Set to the value returned by the previous run to enable incremental ingestion.
    /// </summary>
    public string? DeltaToken { get; set; }
}
