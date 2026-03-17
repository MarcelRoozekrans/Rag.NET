namespace Rag.NET.DataProviders;

/// <summary>Controls whether disappeared documents are deleted from the vector store.</summary>
public enum CleanupMode
{
    /// <summary>No cleanup — disappeared documents are left in the vector store.</summary>
    None,

    /// <summary>
    /// Full cleanup — documents present in the hash store but absent from the current provider
    /// enumeration are deleted from the vector store and removed from the hash store.
    /// Requires <see cref="Rag.NET.Abstractions.IContentHashStore"/> to be registered.
    /// </summary>
    Full,
}
