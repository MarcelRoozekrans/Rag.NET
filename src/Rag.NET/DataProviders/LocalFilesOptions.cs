namespace Rag.NET.DataProviders;

/// <summary>Configuration for <see cref="LocalFilesDataProvider"/>.</summary>
public sealed class LocalFilesOptions
{
    /// <summary>
    /// File extensions to include (e.g. <c>[".md", ".pdf"]</c>).
    /// Defaults to <c>["*"]</c> which matches all extensions.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = ["*"];

    /// <summary>
    /// Whether to enumerate subdirectories. Defaults to <see cref="SearchOption.AllDirectories"/>.
    /// </summary>
    public SearchOption SearchOption { get; init; } = SearchOption.AllDirectories;

    /// <summary>
    /// Optional predicate to exclude files by absolute path.
    /// Return <see langword="false"/> to skip a file.
    /// </summary>
    public Func<string, bool>? Filter { get; init; }
}
