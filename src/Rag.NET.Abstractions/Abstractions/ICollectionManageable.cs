namespace Rag.NET.Abstractions;

public interface ICollectionManageable
{
    Task CreateCollectionAsync(
        string name,
        int vectorDimensions,
        CancellationToken cancellationToken = default);

    Task DeleteCollectionAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<bool> CollectionExistsAsync(
        string name,
        CancellationToken cancellationToken = default);
}
