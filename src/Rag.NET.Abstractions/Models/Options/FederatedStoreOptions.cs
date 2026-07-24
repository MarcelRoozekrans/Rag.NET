namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>FederatedVectorStore</c> (multi-index federation).
/// Validated in the store constructor.
/// </summary>
public sealed class FederatedStoreOptions
{
    /// <summary>
    /// Index of the store that receives writes (<c>StoreAsync</c>) and deletes
    /// (<c>DeleteByDocumentIdAsync</c>). Searches always fan out to all stores.
    /// Must be within the bounds of the store list. Default <c>0</c> (the first store).
    /// </summary>
    public int PrimaryIndex { get; set; }

    /// <summary>
    /// Optional labels for the federated stores, parallel to the store list (same count
    /// when set). Used for the <c>source.store</c> metadata tag on merged results and in
    /// error messages. When <see langword="null"/>, stores are labelled by their index.
    /// </summary>
    public IReadOnlyList<string>? StoreNames { get; set; }

    /// <summary>
    /// Reciprocal Rank Fusion constant <c>k</c> in <c>1 / (k + rank)</c> (1-based ranks).
    /// Higher values flatten the rank contribution curve. Must be at least 1.
    /// Default <c>60</c> (the standard RRF constant).
    /// </summary>
    public int RrfK { get; set; } = 60;
}
