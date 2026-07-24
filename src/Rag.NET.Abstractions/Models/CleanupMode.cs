// Lives in Rag.NET.Abstractions (moved from the core Rag.NET project) so that
// PollingIngestionOptions can reference it without a core dependency. The namespace is kept
// as Rag.NET.DataProviders — the consuming APIs (IngestFromProviderAsync, the polling
// trigger) live there, and keeping it avoids a source-breaking change.
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
