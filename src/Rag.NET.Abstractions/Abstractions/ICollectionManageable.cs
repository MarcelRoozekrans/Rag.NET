namespace Rag.NET.Abstractions;

public interface ICollectionManageable
{
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

    Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default);
}
