namespace Rag.NET.Abstractions;

/// <summary>
/// Optional capability for an <see cref="IVectorStore"/> that can create, check, and delete named
/// collections/indexes in the backend, rather than assuming one pre-provisioned collection exists.
/// </summary>
public interface ICollectionManageable
{
    /// <summary>
    /// Creates a collection sized for <paramref name="vectorDimensions"/>-dimensional vectors.
    /// Whether creating a collection that already exists is an error or a no-op is
    /// implementation-specific.
    /// </summary>
    Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection and all its records. Deleting a collection that does not
    /// exist is a no-op.
    /// </summary>
    Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Reports whether a collection by this name currently exists.</summary>
    Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default);
}
